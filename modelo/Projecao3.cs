// Modelo de projeção da apuração, versão 3 — reconstrução do método de Rodrigo Mahlmeister
// (descrito em projecao.2026elections.workers.dev) com alguns acréscimos.
//
// 1. Swing em razão logarítmica por seção:
//      2º turno:  z = log(Lula / B)                      (B = candidato bolsonarista)
//      1º turno:  z1 = log(Lula / Outros), z2 = log(B / Outros)
//    swing = z(apurado) − z(base), onde a base é a mesma seção na eleição anterior.
// 2. O swing é explicado por uma regressão ponderada pelos votos:
//      swing = β0 + β1·z_base + β2·log(eleitorado do município) [+ β3·swing do 1º turno do mesmo ano]
//    com coeficientes hierárquicos país → região → UF (encolhimento ridge em direção ao nível acima)
//    e um ajuste do município (resíduo médio, encolhido para zero).
// 3. 2º turno: modelo de transferência nas primeiras urnas — cada seção parte do seu 1º turno e os votos
//    dos demais candidatos vão para Lula na proporção observada na parte já apurada da UF (capital e
//    interior separados). Entre 5% e 15% apurado a projeção migra para o modelo principal.
// 4. Faixa de 90%: bootstrap bayesiano (município e seção) + erro sistemático proporcional ao que falta.
// 5. Trajetória: as urnas que faltam chegam em ordem prevista (duas regras disponíveis).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

public class S3 {
  public string key; public int mun, uf, reg, cap;
  public long t; public double ta, tb = double.NaN;
  public double V, L, B, O;            // alvo (votos)
  public double bL, bB, bO, Vb;        // base (votos; agregado do município quando a seção não existe na base)
  public double aL, aB, aO; public bool hasA;   // 1º turno do mesmo ano (só para alvo de 2º turno)
  public bool matched;
  public double[] x;                   // covariáveis por dimensão de swing: x[d*P + j]
  public double[] zb;                  // logit(s) da base por dimensão
  public double[] y;                   // swing observado por dimensão
}

public static class Projecao3 {
  static readonly CultureInfo IC = CultureInfo.InvariantCulture;
  static readonly Dictionary<string, string> Regiao = new Dictionary<string, string> {
    {"AC","N"},{"AM","N"},{"AP","N"},{"PA","N"},{"RO","N"},{"RR","N"},{"TO","N"},
    {"AL","NE"},{"BA","NE"},{"CE","NE"},{"MA","NE"},{"PB","NE"},{"PE","NE"},{"PI","NE"},{"RN","NE"},{"SE","NE"},
    {"DF","CO"},{"GO","CO"},{"MS","CO"},{"MT","CO"},{"ES","SE"},{"MG","SE"},{"RJ","SE"},{"SP","SE"},
    {"PR","S"},{"RS","S"},{"SC","S"},{"ZZ","EX"} };

  public static double lamN = 2000, lamR = 50000, lamU = 50000, kMun = 10000, kTurn = 20000, kTrans = 20000;
  public static double tau = 0.0025;          // erro sistemático: fração dos válidos com 0% apurado
  public static string trajMode = "misto";    // "base", "ritmo15" ou "misto"
  public static bool usarMunicipio = true, usarTransferencia = true, usarRegressao = true;

  class Row { public string uf, mun, z, s; public long t, aptos; public double valid, v13, vB; }
  static List<Row> Load(string path) {
    var L = new List<Row>();
    if (string.IsNullOrEmpty(path)) return L;
    foreach (var line in File.ReadLines(path)) {
      var c = line.Split('\t');
      L.Add(new Row { uf = c[0], mun = c[1], z = c[2], s = c[3], t = long.Parse(c[4]), aptos = long.Parse(c[5]),
                      valid = double.Parse(c[7], IC), v13 = double.Parse(c[8], IC), vB = double.Parse(c[9], IC) });
    }
    return L;
  }
  static string K4(Row r) { return r.uf + "|" + r.mun + "|" + r.z + "|" + r.s; }

  // Resolve (A + lam I) beta = c + lam prior, A p×p simétrica
  static double[] Solve(double[,] A, double[] c, double lam, double[] prior, int P) {
    var M = new double[P, P + 1];
    for (int i = 0; i < P; i++) { for (int j = 0; j < P; j++) M[i, j] = A[i, j] + (i == j ? lam : 0); M[i, P] = c[i] + lam * prior[i]; }
    for (int col = 0; col < P; col++) {
      int piv = col; for (int r = col + 1; r < P; r++) if (Math.Abs(M[r, col]) > Math.Abs(M[piv, col])) piv = r;
      if (Math.Abs(M[piv, col]) < 1e-12) continue;
      if (piv != col) for (int j = 0; j <= P; j++) { var tmp = M[col, j]; M[col, j] = M[piv, j]; M[piv, j] = tmp; }
      for (int r = 0; r < P; r++) if (r != col) { double f = M[r, col] / M[col, col]; for (int j = col; j <= P; j++) M[r, j] -= f * M[col, j]; }
    }
    var b = new double[P]; for (int i = 0; i < P; i++) b[i] = Math.Abs(M[i, i]) < 1e-12 ? prior[i] : M[i, P] / M[i, i];
    return b;
  }

  public static string Run(string targetPath, string basePath, string auxPath, string orderPath, string mode, int Bn, int seed) {
    bool T2 = mode == "2T"; int D = T2 ? 1 : 2;
    var tgt = Load(targetPath); var bas = Load(basePath); var aux = Load(auxPath); var ord = Load(orderPath);
    var baseSec = new Dictionary<string, Row>(); var munB = new Dictionary<string, double[]>(); var ufB = new Dictionary<string, double[]>();
    foreach (var r in bas) {
      baseSec[K4(r)] = r;
      foreach (var kv in new[] { new { d = munB, k = r.uf + "|" + r.mun }, new { d = ufB, k = r.uf } }) {
        double[] a; if (!kv.d.TryGetValue(kv.k, out a)) kv.d[kv.k] = a = new double[4];
        a[0] += r.aptos; a[1] += r.valid; a[2] += r.v13; a[3] += r.vB;
      }
    }
    var auxSec = new Dictionary<string, Row>(); foreach (var r in aux) auxSec[K4(r)] = r;
    var ordSec = new Dictionary<string, Row>(); foreach (var r in ord) ordSec[K4(r)] = r;
    Func<IEnumerable<long>, long> DayStart = ts => { var v = ts.Where(x => x > 0).OrderBy(x => x).ToArray(); return v.Length == 0 ? 0 : (v[v.Length / 2] / 86400) * 86400; };
    long tgtDay = DayStart(tgt.Select(r => r.t)), ordDay = DayStart(ord.Select(r => r.t));

    var munIdx = new Dictionary<string, int>(); var ufIdx = new Dictionary<string, int>(); var regIdx = new Dictionary<string, int>();
    var munApt = new Dictionary<string, double>();
    foreach (var r in tgt) { string mk = r.uf + "|" + r.mun; double a; munApt.TryGetValue(mk, out a); munApt[mk] = a + r.aptos; }
    var secs = new List<S3>(); int matched = 0;
    foreach (var r in tgt) {
      double V = T2 ? r.v13 + r.vB : r.valid; if (V <= 0) continue;
      string mk = r.uf + "|" + r.mun;
      var s = new S3 { key = K4(r), t = r.t, V = V, L = r.v13, B = r.vB, O = Math.Max(0, r.valid - r.v13 - r.vB) };
      if (!munIdx.ContainsKey(mk)) munIdx[mk] = munIdx.Count; s.mun = munIdx[mk];
      if (!ufIdx.ContainsKey(r.uf)) ufIdx[r.uf] = ufIdx.Count; s.uf = ufIdx[r.uf];
      string rg = Regiao.ContainsKey(r.uf) ? Regiao[r.uf] : "EX"; if (!regIdx.ContainsKey(rg)) regIdx[rg] = regIdx.Count; s.reg = regIdx[rg];
      Row br; double[] ag;
      if (baseSec.TryGetValue(s.key, out br) && (T2 ? br.v13 + br.vB : br.valid) > 0) {
        s.bL = br.v13; s.bB = br.vB; s.bO = Math.Max(0, br.valid - br.v13 - br.vB); s.Vb = T2 ? br.v13 + br.vB : br.valid; s.matched = true; matched++;
      } else if (munB.TryGetValue(mk, out ag) || ufB.TryGetValue(r.uf, out ag)) {
        double f = ag[0] > 0 ? r.aptos / ag[0] : 0; s.bL = ag[2] * f; s.bB = ag[3] * f; s.bO = Math.Max(0, ag[1] - ag[2] - ag[3]) * f; s.Vb = T2 ? s.bL + s.bB : ag[1] * f;
      } else { s.bL = r.v13; s.bB = r.vB; s.bO = s.O; s.Vb = V; }
      if (s.Vb <= 0) s.Vb = V;
      Row ar; if (auxSec.TryGetValue(s.key, out ar) && ar.valid > 0) { s.aL = ar.v13; s.aB = ar.vB; s.aO = Math.Max(0, ar.valid - ar.v13 - ar.vB); s.hasA = true; }
      Row orr; if (ordSec.TryGetValue(s.key, out orr) && orr.t > 0) s.tb = orr.t - ordDay;
      s.ta = r.t - tgtDay;
      secs.Add(s);
    }
    secs = secs.OrderBy(x => x.t).ThenBy(x => x.key, StringComparer.Ordinal).ToList();
    int N = secs.Count, nM = munIdx.Count, nU = ufIdx.Count, nR = regIdx.Count;
    var ufReg = new int[nU]; var munUF = new int[nM]; foreach (var s in secs) { ufReg[s.uf] = s.reg; munUF[s.mun] = s.uf; }
    // capital ≈ maior município da UF em eleitorado
    var capMun = new int[nU]; var capApt = new double[nU];
    foreach (var kv in munIdx) { int u = ufIdx[kv.Key.Split('|')[0]]; if (munApt[kv.Key] > capApt[u]) { capApt[u] = munApt[kv.Key]; capMun[u] = kv.Value; } }
    var munSize = new double[nM]; foreach (var kv in munIdx) munSize[kv.Value] = Math.Log(Math.Max(1, munApt[kv.Key]));
    foreach (var s in secs) s.cap = s.mun == capMun[s.uf] ? 1 : 0;
    // horário na base de ordem para seções sem correspondência: média do município/UF
    { var mS = new double[nM]; var mC = new double[nM]; var uS = new double[nU]; var uC = new double[nU];
      foreach (var s in secs) if (!double.IsNaN(s.tb)) { mS[s.mun] += s.tb; mC[s.mun]++; uS[s.uf] += s.tb; uC[s.uf]++; }
      double all = secs.Where(s => !double.IsNaN(s.tb)).Select(s => s.tb).DefaultIfEmpty(double.NaN).Average();
      foreach (var s in secs) if (double.IsNaN(s.tb)) s.tb = mC[s.mun] > 0 ? mS[s.mun] / mC[s.mun] : uC[s.uf] > 0 ? uS[s.uf] / uC[s.uf] : all; }
    bool temOrdem = secs.Any(s => !double.IsNaN(s.tb));

    // Covariáveis e swing observado (log-razões com suavização de meio voto)
    const double e = 0.5;
    bool comAux = T2 && secs.Count(s => s.hasA) > N / 2;
    int P = comAux ? 4 : 3;   // [1, z_base, log(eleitorado mun.), (swing 1º turno)]
    if (!T2) P = 4;          // 1º turno: [1, z1_base, z2_base, log(eleitorado mun.)]
    foreach (var s in secs) {
      s.zb = T2 ? new[] { Math.Log((s.bL + e) / (s.bB + e)) } : new[] { Math.Log((s.bL + e) / (s.bO + e)), Math.Log((s.bB + e) / (s.bO + e)) };
      s.y = T2 ? new[] { Math.Log((s.L + e) / (s.B + e)) - s.zb[0] } : new[] { Math.Log((s.L + e) / (s.O + e)) - s.zb[0], Math.Log((s.B + e) / (s.O + e)) - s.zb[1] };
      s.x = new double[D * P];
    }
    // centraliza covariáveis pela média nacional ponderada pela base
    double wsum = secs.Sum(s => s.Vb); double mSize = secs.Sum(s => s.Vb * munSize[s.mun]) / wsum;
    var mZ = new double[D]; for (int d = 0; d < D; d++) mZ[d] = secs.Sum(s => s.Vb * s.zb[d]) / wsum;
    double mAux = 0; if (comAux) mAux = secs.Where(s => s.hasA).Sum(s => s.Vb * (Math.Log((s.aL + e) / (s.aB + e)) - s.zb[0])) / Math.Max(1, secs.Where(s => s.hasA).Sum(s => s.Vb));
    foreach (var s in secs) for (int d = 0; d < D; d++) {
      int o = d * P; s.x[o] = 1;
      if (!usarRegressao) continue;
      if (T2) { s.x[o + 1] = s.zb[0] - mZ[0]; s.x[o + 2] = munSize[s.mun] - mSize; if (comAux) s.x[o + 3] = s.hasA ? Math.Log((s.aL + e) / (s.aB + e)) - s.zb[0] - mAux : 0; }
      else { s.x[o + 1] = s.zb[0] - mZ[0]; s.x[o + 2] = s.zb[1] - mZ[1]; s.x[o + 3] = munSize[s.mun] - mSize; }
    }
    int Kc = T2 ? 2 : 3;
    Func<S3, double[], double[]> Shares = (s, z) => {   // z = logits previstos -> participação [L, B, (O)]
      if (T2) { double pL = 1 / (1 + Math.Exp(-z[0])); return new[] { pL, 1 - pL }; }
      double a = Math.Exp(z[0]), b = Math.Exp(z[1]), den = a + b + 1; return new[] { a / den, b / den, 1 / den };
    };
    double[] final = new double[Kc]; { double vt = 0; foreach (var s in secs) { final[0] += s.L; final[1] += s.B; if (!T2) final[2] += s.O; vt += s.V; } for (int k = 0; k < Kc; k++) final[k] /= vt; }

    double[] capY = null, capV = null;
    Func<int, double[], double[], double[]> Project = (n, wMun, wSec) => {
      // 1) Regressão hierárquica: acumula X'WX e X'Wy por nível
      var AN = new double[D, P, P]; var cN = new double[D, P];
      var AR = new double[nR, D, P, P]; var cR = new double[nR, D, P];
      var AU = new double[nU, D, P, P]; var cU = new double[nU, D, P];
      var tot = new double[Kc]; var Vc = new double[4][]; // comparecimento: [mun, uf, reg] somas V e Vb
      var VM = new double[nM]; var VbM = new double[nM]; var VU = new double[nU]; var VbU = new double[nU]; var VR = new double[nR]; var VbR = new double[nR]; double VN = 0, VbN = 0;
      // transferência: por UF×capital: soma L2, V2, L1, O1, V1
      var TR = new double[nU * 2, 5];
      for (int i = 0; i < n; i++) {
        var s = secs[i]; tot[0] += s.L; tot[1] += s.B; if (!T2) tot[2] += s.O;
        double wv = wMun == null ? 1 : wMun[s.mun] * wSec[i], w = s.V * wv;
        for (int d = 0; d < D; d++) { int o = d * P;
          for (int a = 0; a < P; a++) { double xa = s.x[o + a]; if (xa == 0 && a > 0) continue;
            double cy = w * xa * s.y[d]; cN[d, a] += cy; cR[s.reg, d, a] += cy; cU[s.uf, d, a] += cy;
            for (int b = 0; b < P; b++) { double v = w * xa * s.x[o + b]; AN[d, a, b] += v; AR[s.reg, d, a, b] += v; AU[s.uf, d, a, b] += v; } } }
        double vb = s.hasA ? s.aL + s.aB + (T2 ? 0 : s.aO) : s.Vb; if (T2 && s.hasA) vb = s.aL + s.aB + s.aO;
        VM[s.mun] += wv * s.V; VbM[s.mun] += wv * vb; VU[s.uf] += wv * s.V; VbU[s.uf] += wv * vb; VR[s.reg] += wv * s.V; VbR[s.reg] += wv * vb; VN += wv * s.V; VbN += wv * vb;
        if (T2 && s.hasA) { int g = s.uf * 2 + s.cap; TR[g, 0] += wv * s.L; TR[g, 1] += wv * s.V; TR[g, 2] += wv * s.aL; TR[g, 3] += wv * s.aO; TR[g, 4] += wv * (s.aL + s.aB + s.aO); }
      }
      var zero = new double[P];
      var bN = new double[D][]; var bR = new double[nR, D][]; var bU = new double[nU, D][];
      for (int d = 0; d < D; d++) {
        var A = new double[P, P]; var c = new double[P];
        for (int a = 0; a < P; a++) { c[a] = cN[d, a]; for (int b = 0; b < P; b++) A[a, b] = AN[d, a, b]; }
        bN[d] = Solve(A, c, lamN, zero, P);
        for (int g = 0; g < nR; g++) { for (int a = 0; a < P; a++) { c[a] = cR[g, d, a]; for (int b = 0; b < P; b++) A[a, b] = AR[g, d, a, b]; } bR[g, d] = Solve(A, c, lamR, bN[d], P); }
        for (int g = 0; g < nU; g++) { for (int a = 0; a < P; a++) { c[a] = cU[g, d, a]; for (int b = 0; b < P; b++) A[a, b] = AU[g, d, a, b]; } bU[g, d] = Solve(A, c, lamU, bR[ufReg[g], d], P); }
      }
      // 2) ajuste do município
      var SM = new double[nM, D]; var WM = new double[nM];
      if (usarMunicipio) for (int i = 0; i < n; i++) {
        var s = secs[i]; double w = s.V * (wMun == null ? 1 : wMun[s.mun] * wSec[i]); WM[s.mun] += w;
        for (int d = 0; d < D; d++) { double pr = 0; var bb = bU[s.uf, d]; for (int a = 0; a < P; a++) pr += bb[a] * s.x[d * P + a]; SM[s.mun, d] += w * (s.y[d] - pr); }
      }
      // 3) comparecimento (votos válidos) hierárquico
      double rN = VbN > 0 ? VN / VbN : 1;
      var rR = new double[nR]; for (int g = 0; g < nR; g++) rR[g] = (VR[g] + kTurn * rN) / (VbR[g] + kTurn);
      var rU = new double[nU]; for (int g = 0; g < nU; g++) rU[g] = (VU[g] + kTurn * rR[ufReg[g]]) / (VbU[g] + kTurn);
      var rM = new double[nM]; for (int g = 0; g < nM; g++) rM[g] = (VM[g] + kTurn * rU[munUF[g]]) / (VbM[g] + kTurn);
      // 4) transferência (2º turno): fração dos "outros" do 1º turno que vai para Lula
      double pct = 100.0 * n / N, wMain = (!T2 || !comAux || !usarTransferencia) ? 1 : Math.Max(0, Math.Min(1, (pct - 5) / 10.0));
      var tauG = new double[nU * 2];
      if (wMain < 1) {
        double nL2 = 0, nV2 = 0, nL1 = 0, nO1 = 0, nV1 = 0; for (int g = 0; g < nU * 2; g++) { nL2 += TR[g, 0]; nV2 += TR[g, 1]; nL1 += TR[g, 2]; nO1 += TR[g, 3]; nV1 += TR[g, 4]; }
        Func<double, double, double, double, double, double> Tf = (l2, v2, l1, o1, v1) => o1 > 0 && v2 > 0 ? (l2 / v2 * v1 - l1) / o1 : 0.5;
        double tN = Math.Max(0, Math.Min(1, Tf(nL2, nV2, nL1, nO1, nV1)));
        for (int u = 0; u < nU; u++) {
          double l2 = TR[2 * u, 0] + TR[2 * u + 1, 0], v2 = TR[2 * u, 1] + TR[2 * u + 1, 1], l1 = TR[2 * u, 2] + TR[2 * u + 1, 2], o1 = TR[2 * u, 3] + TR[2 * u + 1, 3], v1 = TR[2 * u, 4] + TR[2 * u + 1, 4];
          double tU = (o1 * Math.Max(0, Math.Min(1, Tf(l2, v2, l1, o1, v1))) + kTrans * tN) / (o1 + kTrans);
          for (int c = 0; c < 2; c++) { int g = 2 * u + c; double tg = Math.Max(0, Math.Min(1, Tf(TR[g, 0], TR[g, 1], TR[g, 2], TR[g, 3], TR[g, 4]))); tauG[g] = (TR[g, 3] * tg + kTrans * tU) / (TR[g, 3] + kTrans); }
        }
      }
      // 5) previsão das seções que faltam
      var z = new double[D];
      for (int i = n; i < N; i++) {
        var s = secs[i];
        for (int d = 0; d < D; d++) { double pr = 0; var bb = bU[s.uf, d]; for (int a = 0; a < P; a++) pr += bb[a] * s.x[d * P + a]; z[d] = s.zb[d] + pr + (usarMunicipio ? SM[s.mun, d] / (WM[s.mun] + kMun) : 0); }
        var sh = Shares(s, z);
        if (wMain < 1 && s.hasA) { double v1 = s.aL + s.aB + s.aO; if (v1 > 0) { double pL = (s.aL + tauG[s.uf * 2 + s.cap] * s.aO) / v1; sh[0] = wMain * sh[0] + (1 - wMain) * pL; sh[1] = 1 - sh[0]; } }
        double vb = s.hasA ? s.aL + s.aB + s.aO : s.Vb; double Vh = vb * rM[s.mun];
        for (int k = 0; k < Kc; k++) tot[k] += Vh * sh[k];
        if (capY != null) { for (int k = 0; k < Kc; k++) capY[i * Kc + k] = sh[k]; capV[i] = Vh; }
      }
      double T = tot.Sum(); for (int k = 0; k < Kc; k++) tot[k] /= T;
      return tot;
    };

    var sb = new StringBuilder();
    sb.Append("{\"secoes\":").Append(N).Append(",\"comBase\":").Append(matched);
    sb.Append(",\"final\":[").Append(string.Join(",", final.Select(v => (100 * v).ToString("0.000", IC)))).Append("]");
    sb.Append(",\"modelo\":{\"versao\":3,\"regressao\":").Append(usarRegressao ? "true" : "false").Append(",\"municipio\":").Append(usarMunicipio ? "true" : "false")
      .Append(",\"transferencia\":").Append(comAux && usarTransferencia ? "true" : "false").Append(",\"trajetoria\":\"").Append(trajMode).Append("\"}");
    var curve = new List<string>(); var proj = new List<string>();
    { var cnt = new double[Kc]; double cv = 0; int next = 1;
      for (int i = 0; i < N; i++) { var s = secs[i]; cnt[0] += s.L; cnt[1] += s.B; if (!T2) cnt[2] += s.O; cv += s.V;
        while (next <= 200 && (i + 1) >= (long)Math.Ceiling(N * next / 200.0)) {
          double pct = next * 0.5; int n = i + 1; double cvv = cv;
          curve.Add("[" + pct.ToString("0.0", IC) + "," + string.Join(",", cnt.Select(v => (100 * v / cvv).ToString("0.000", IC))) + ",\"" + Hora(s.ta) + "\"]");
          if (next < 200) { var p = Project(n, null, null); proj.Add("[" + pct.ToString("0.0", IC) + "," + string.Join(",", p.Select(v => (100 * v).ToString("0.000", IC))) + "]"); }
          next++; } } }
    sb.Append(",\"curve\":[").Append(string.Join(",", curve)).Append("]");
    sb.Append(",\"proj\":[").Append(string.Join(",", proj)).Append("]");

    // Faixa de 90%
    var rnd = new Random(seed); var band = new List<string>(); var wM = new double[nM]; var wS = new double[N];
    for (int step = 1; step < 40; step++) {
      double pct = step * 2.5; int n = (int)Math.Ceiling(N * pct / 100.0);
      var reps = new double[Math.Max(1, Bn)][];
      for (int b = 0; b < reps.Length; b++) { for (int m = 0; m < nM; m++) wM[m] = -Math.Log(1 - rnd.NextDouble()); for (int i = 0; i < n; i++) wS[i] = -Math.Log(1 - rnd.NextDouble()); reps[b] = Project(n, wM, wS); }
      var center = Project(n, null, null); double sys = 1.645 * tau * (1 - pct / 100.0); var parts = new List<string>();
      for (int k = 0; k < Kc; k++) {
        var v = reps.Select(r => r[k]).OrderBy(x => x).ToArray(); int L = v.Length;
        double q05 = v[(int)(0.05 * (L - 1))], q95 = v[(int)Math.Ceiling(0.95 * (L - 1))], c = center[k];
        parts.Add((100 * (c - Math.Sqrt(Math.Pow(Math.Max(0, c - q05), 2) + sys * sys))).ToString("0.000", IC));
        parts.Add((100 * (c + Math.Sqrt(Math.Pow(Math.Max(0, q95 - c), 2) + sys * sys))).ToString("0.000", IC));
      }
      band.Add("[" + pct.ToString("0.0", IC) + "," + string.Join(",", parts) + "]");
    }
    sb.Append(",\"band\":[").Append(string.Join(",", band)).Append("]");

    // Trajetória prevista
    var traj = new List<string>();
    for (int cpi = 1; cpi <= 19; cpi++) {
      double cp = cpi * 5; int n = (int)Math.Ceiling(N * cp / 100.0);
      capY = new double[N * Kc]; capV = new double[N]; Project(n, null, null); var cy = capY; var cvv = capV; capY = null; capV = null;
      double now = secs[n - 1].ta; var pred = new double[N];
      var cntU = new int[nU]; var totU = new int[nU]; for (int i = 0; i < N; i++) { totU[secs[i].uf]++; if (i < n) cntU[secs[i].uf]++; }
      var predR = new double[N]; var predB = new double[N];
      bool fazR = trajMode != "base" || !temOrdem, fazB = temOrdem && trajMode != "ritmo15";
      if (fazR) {
        // cada UF mantém o ritmo dos últimos 15 minutos; dentro da UF, ordem da base (ou uniforme)
        var rec = new int[nU]; for (int i = n - 1; i >= 0 && secs[i].ta > now - 900; i--) rec[secs[i].uf]++;
        double recTot = rec.Sum(), remTot = N - n;
        var rate = new double[nU]; for (int u = 0; u < nU; u++) { int rem = totU[u] - cntU[u]; rate[u] = rec[u] > 0 ? rec[u] / 900.0 : (recTot > 0 ? Math.Max(1e-6, rem * (recTot / 900.0) / Math.Max(1, remTot)) : 1e-3); }
        var porUF = Enumerable.Range(n, N - n).GroupBy(i => secs[i].uf);
        foreach (var g in porUF) { int j = 0; foreach (var i in g.OrderBy(i => temOrdem ? secs[i].tb : 0).ThenBy(i => (secs[i].key.GetHashCode() & 0x7fffffff))) { j++; predR[i] = now + j / rate[g.Key]; } }
      }
      if (fazB) {
        // ordem da base: cada UF continua do ponto da base em que está, no ritmo observado
        var tbU = new List<double>[nU]; for (int u = 0; u < nU; u++) tbU[u] = new List<double>(); foreach (var s in secs) tbU[s.uf].Add(s.tb);
        var tauU = new double[nU]; for (int u = 0; u < nU; u++) { tbU[u].Sort(); int q = Math.Min(tbU[u].Count - 1, (int)((long)cntU[u] * tbU[u].Count / Math.Max(1, totU[u]))); tauU[u] = tbU[u].Count > 0 ? tbU[u][Math.Max(0, q)] : 0; }
        var tbAll = secs.Select(s => s.tb).OrderBy(x => x).ToArray();
        double a0 = secs[n / 2].ta, b0 = tbAll[n / 2], bNow = tbAll[Math.Min(N - 1, n)];
        double ritmo = (bNow - b0) > 60 ? Math.Max(0.3, Math.Min(3, (now - a0) / (bNow - b0))) : 1;
        for (int i = n; i < N; i++) predB[i] = now + Math.Max(0, secs[i].tb - tauU[secs[i].uf]) * ritmo;
      }
      // "misto": no começo vale a ordem da base; o ritmo recente de cada UF ganha peso até 40% apurado
      // A combinação é feita pela posição na fila (0 a 1), para um estado lento não dominar a média de horários.
      double wR = !fazB ? 1 : !fazR ? 0 : Math.Max(0, Math.Min(1, cp / 40.0));
      var posB = new double[N]; var posR = new double[N]; int nRest = N - n;
      if (fazB) { var o = Enumerable.Range(n, nRest).OrderBy(i => predB[i]).ThenBy(i => secs[i].key, StringComparer.Ordinal).ToArray(); for (int r = 0; r < nRest; r++) posB[o[r]] = (double)r / nRest; }
      if (fazR) { var o = Enumerable.Range(n, nRest).OrderBy(i => predR[i]).ThenBy(i => secs[i].key, StringComparer.Ordinal).ToArray(); for (int r = 0; r < nRest; r++) posR[o[r]] = (double)r / nRest; }
      var rest = Enumerable.Range(n, nRest).OrderBy(i => (1 - wR) * posB[i] + wR * posR[i]).ThenBy(i => secs[i].key, StringComparer.Ordinal).ToArray();
      // horário previsto de cada posição da fila: vem da regra que pesa mais neste momento
      { bool usaB = fazB && (!fazR || wR < 0.5);
        var tt = Enumerable.Range(n, nRest).Select(i => usaB ? predB[i] : predR[i]).OrderBy(x => x).ToArray();
        for (int r = 0; r < nRest; r++) pred[rest[r]] = tt[r]; }
      var acc = new double[Kc]; for (int i = 0; i < n; i++) { acc[0] += secs[i].L; acc[1] += secs[i].B; if (!T2) acc[2] += secs[i].O; }
      int lead = acc[0] >= acc[1] ? 0 : 1, lead0 = lead; var vir = new List<double[]>(); var virH = new List<string>(); var pts = new List<string>(); int nextPct = (int)cp + 1;
      for (int j = 0; j < rest.Length; j++) {
        int i = rest[j]; for (int k = 0; k < Kc; k++) acc[k] += cvv[i] * cy[i * Kc + k];
        double pc = 100.0 * (n + j + 1) / N; int l = acc[0] >= acc[1] ? 0 : 1;
        if (l != lead) { vir.Add(new double[] { pc, l }); virH.Add(Hora(pred[i])); lead = l; }
        if (pc >= nextPct || j == rest.Length - 1) { double T = acc.Sum(); pts.Add("[" + pc.ToString("0.0", IC) + "," + string.Join(",", acc.Select(v => (100 * v / T).ToString("0.000", IC))) + ",\"" + Hora(pred[i]) + "\"]"); while (nextPct <= pc) nextPct++; }
      }
      traj.Add("{\"cp\":" + cp.ToString(IC) + ",\"pts\":[" + string.Join(",", pts) + "],\"viradas\":[" + Viradas(vir, virH, lead0) + "]}");
    }
    var virR = new List<double[]>(); var virRH = new List<string>(); int ld0 = -1;
    { double aL = 0, aB = 0; int ld = -1; for (int i = 0; i < N; i++) { aL += secs[i].L; aB += secs[i].B; int l = aL >= aB ? 0 : 1; if (ld >= 0 && l != ld) { virR.Add(new double[] { 100.0 * (i + 1) / N, l }); virRH.Add(Hora(secs[i].ta)); } if (ld < 0) ld0 = l; ld = l; } }
    sb.Append(",\"traj\":[").Append(string.Join(",", traj)).Append("]");
    sb.Append(",\"viradasReais\":[").Append(Viradas(virR, virRH, ld0)).Append("]}");
    return sb.ToString();
  }

  static string Hora(double sec) { long x = (long)Math.Round(sec); x = ((x % 86400) + 86400) % 86400; return (x / 3600).ToString("00") + ":" + (x % 3600 / 60).ToString("00"); }
  static string Viradas(List<double[]> v, List<string> h, int lead0) {
    var o = new List<string>(); int lead = lead0, i = 0;
    while (i < v.Count) { int j = i; while (j + 1 < v.Count && v[j + 1][0] - v[j][0] < 0.5) j++; int after = (int)v[j][1];
      if (after != lead) { o.Add("[" + v[i][0].ToString("0.00", IC) + ",\"" + h[i] + "\"," + after + "]"); lead = after; } i = j + 1; }
    return string.Join(",", o);
  }
}
