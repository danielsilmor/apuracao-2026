// Modelo de projeção da apuração por swing seção a seção (inspirado no método de Rodrigo Mahlmeister).
// Para cada seção já apurada mede-se a mudança (swing) em relação à mesma seção na eleição de base.
// Para as seções ainda não apuradas, aplica-se o swing estimado do município, encolhido em direção
// ao do estado, da região e do país quando há poucos dados (estimativa hierárquica).
// O comparecimento (votos válidos) também é ajustado pela variação observada no grupo.
// A faixa de incerteza vem de um bootstrap bayesiano (pesos Exp(1) por município e por seção).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

public class Sec {
  public string key, uf; public int mun, uf_i, reg_i;
  public long t, aptos; public double V; public double[] y;   // alvo: votos válidos e participação por categoria
  public double Vb; public double[] b; public bool matched;   // base
}

public static class Projecao {
  static readonly Dictionary<string, string> Regiao = new Dictionary<string, string> {
    {"AC","N"},{"AM","N"},{"AP","N"},{"PA","N"},{"RO","N"},{"RR","N"},{"TO","N"},
    {"AL","NE"},{"BA","NE"},{"CE","NE"},{"MA","NE"},{"PB","NE"},{"PE","NE"},{"PI","NE"},{"RN","NE"},{"SE","NE"},
    {"DF","CO"},{"GO","CO"},{"MS","CO"},{"MT","CO"},
    {"ES","SE"},{"MG","SE"},{"RJ","SE"},{"SP","SE"},
    {"PR","S"},{"RS","S"},{"SC","S"},{"ZZ","EX"} };
  static readonly CultureInfo IC = CultureInfo.InvariantCulture;

  // modo "1T": categorias [13, 22, outros] (% dos válidos). modo "2T": [13, 22] com base no 1º turno (dois candidatos).
  static double[] Shares(string mode, double valid, double v13, double v22) {
    if (mode == "1T") { if (valid <= 0) return null; return new[] { v13 / valid, v22 / valid, Math.Max(0, valid - v13 - v22) / valid }; }
    double s = v13 + v22; if (s <= 0) return null; return new[] { v13 / s, v22 / s };
  }

  class Row { public string uf, mun, z, s; public long t, aptos; public double valid, v13, v22; }
  static List<Row> Load(string path) {
    var L = new List<Row>();
    foreach (var line in File.ReadLines(path)) {
      var c = line.Split('\t');
      L.Add(new Row { uf = c[0], mun = c[1], z = c[2], s = c[3], t = long.Parse(c[4]), aptos = long.Parse(c[5]),
                      valid = double.Parse(c[7], IC), v13 = double.Parse(c[8], IC), v22 = double.Parse(c[9], IC) });
    }
    return L;
  }

  // Parâmetros de encolhimento (em votos válidos "virtuais").
  public static double kMun = 10000, kUF = 20000, kReg = 100000;
  // Erro sistemático (fração dos votos válidos) por unidade de "falta apurar": 0,25 p.p. com 0% apurado.
  public static double tau = 0.0025;

  public static string Run(string targetPath, string basePath, string mode, string targetVotesMode, int B, int seed) {
    var tgt = Load(targetPath); var bas = Load(basePath);
    int K = mode == "1T" ? 3 : 2;

    // Base por seção e agregados de base por município e UF (para seções sem correspondência)
    var baseSec = new Dictionary<string, Row>();
    var munB = new Dictionary<string, double[]>(); var ufB = new Dictionary<string, double[]>();   // [aptos, valid, v13, v22]
    foreach (var r in bas) {
      baseSec[r.uf + "|" + r.mun + "|" + r.z + "|" + r.s] = r;
      foreach (var kv in new[] { new { d = munB, k = r.uf + "|" + r.mun }, new { d = ufB, k = r.uf } }) {
        double[] a; if (!kv.d.TryGetValue(kv.k, out a)) kv.d[kv.k] = a = new double[4];
        a[0] += r.aptos; a[1] += r.valid; a[2] += r.v13; a[3] += r.v22;
      }
    }

    // Monta seções-alvo
    var munIdx = new Dictionary<string, int>(); var ufIdx = new Dictionary<string, int>(); var regIdx = new Dictionary<string, int>();
    var secs = new List<Sec>(); int matched = 0, semBase = 0;
    foreach (var r in tgt) {
      var y = Shares(targetVotesMode, r.valid, r.v13, r.v22);
      if (y == null) continue;   // seção sem votos válidos (não altera a projeção)
      var s = new Sec { key = r.uf + "|" + r.mun + "|" + r.z + "|" + r.s, uf = r.uf, t = r.t, aptos = r.aptos, V = targetVotesMode == "1T" ? r.valid : r.v13 + r.v22, y = y };
      string mk = r.uf + "|" + r.mun;
      if (!munIdx.ContainsKey(mk)) munIdx[mk] = munIdx.Count; s.mun = munIdx[mk];
      if (!ufIdx.ContainsKey(r.uf)) ufIdx[r.uf] = ufIdx.Count; s.uf_i = ufIdx[r.uf];
      string rg = Regiao.ContainsKey(r.uf) ? Regiao[r.uf] : "EX";
      if (!regIdx.ContainsKey(rg)) regIdx[rg] = regIdx.Count; s.reg_i = regIdx[rg];
      Row br; double[] agg;
      if (baseSec.TryGetValue(s.key, out br) && (s.b = Shares(mode, br.valid, br.v13, br.v22)) != null) {
        s.Vb = mode == "1T" ? br.valid : br.v13 + br.v22; s.matched = true; matched++;
      } else if ((munB.TryGetValue(mk, out agg) || ufB.TryGetValue(r.uf, out agg)) && (s.b = Shares(mode, agg[1], agg[2], agg[3])) != null) {
        double vb = mode == "1T" ? agg[1] : agg[2] + agg[3];
        s.Vb = agg[0] > 0 ? r.aptos * vb / agg[0] : vb; semBase++;
      } else { s.b = (double[])y.Clone(); s.Vb = s.V; semBase++; }
      if (s.Vb <= 0) s.Vb = Math.Max(1, s.V);
      secs.Add(s);
    }
    // Ordem de chegada ao TSE
    secs = secs.OrderBy(x => x.t).ThenBy(x => x.key, StringComparer.Ordinal).ToList();
    int N = secs.Count, nM = munIdx.Count, nU = ufIdx.Count, nR = regIdx.Count;

    // Hierarquia: região de cada UF e UF de cada município
    var ufReg = new int[nU]; var munUF = new int[nM];
    foreach (var s in secs) { ufReg[s.uf_i] = s.reg_i; munUF[s.mun] = s.uf_i; }

    double[] final = new double[K]; double vt = 0;
    foreach (var s in secs) { for (int k = 0; k < K; k++) final[k] += s.V * s.y[k]; vt += s.V; }
    for (int k = 0; k < K; k++) final[k] /= vt;

    // Perfil de votação: faixa do voto anterior da seção no candidato 13 (Lula)
    const int NB = 6;
    Func<double, int> BinOf = v => v < 0.30 ? 0 : v < 0.40 ? 1 : v < 0.50 ? 2 : v < 0.60 ? 3 : v < 0.70 ? 4 : 5;
    var bin = new int[N]; for (int i = 0; i < N; i++) bin[i] = BinOf(secs[i].b[0]);

    // Projeção para os primeiros n apurados, com pesos opcionais (bootstrap).
    // Swing = efeito do perfil (faixa de voto anterior) estimado por UF, encolhido para região e país,
    //         + ajuste do município (resíduo médio do município, encolhido para zero).
    Func<int, double[], double[], double[]> Project = (n, wMun, wSec) => {
      var SNb = new double[NB, K]; var WNb = new double[NB]; var SN = new double[K]; double WN = 0;
      var SRb = new double[nR * NB, K]; var WRb = new double[nR * NB];
      var SUb = new double[nU * NB, K]; var WUb = new double[nU * NB];
      var VM = new double[nM]; var VbM = new double[nM]; var VU = new double[nU]; var VbU = new double[nU];
      var VR = new double[nR]; var VbR = new double[nR]; double VN = 0, VbN = 0;
      var tot = new double[K];
      for (int i = 0; i < n; i++) {
        var s = secs[i]; int bi = bin[i];
        for (int k = 0; k < K; k++) tot[k] += s.V * s.y[k];
        double wv = (wMun == null ? 1 : wMun[s.mun] * wSec[i]); double w = s.V * wv;
        int ri = s.reg_i * NB + bi, ui = s.uf_i * NB + bi;
        for (int k = 0; k < K; k++) { double d = w * (s.y[k] - s.b[k]); SNb[bi, k] += d; SN[k] += d; SRb[ri, k] += d; SUb[ui, k] += d; }
        WNb[bi] += w; WN += w; WRb[ri] += w; WUb[ui] += w;
        VM[s.mun] += wv * s.V; VbM[s.mun] += wv * s.Vb; VU[s.uf_i] += wv * s.V; VbU[s.uf_i] += wv * s.Vb;
        VR[s.reg_i] += wv * s.V; VbR[s.reg_i] += wv * s.Vb; VN += wv * s.V; VbN += wv * s.Vb;
      }
      // efeito de perfil: país -> região -> UF
      var dN = new double[K]; for (int k = 0; k < K; k++) dN[k] = WN > 0 ? SN[k] / WN : 0;
      var dNb = new double[NB, K];
      for (int b = 0; b < NB; b++) for (int k = 0; k < K; k++) dNb[b, k] = (SNb[b, k] + kReg * dN[k]) / (WNb[b] + kReg);
      var dRb = new double[nR * NB, K];
      for (int g = 0; g < nR * NB; g++) { int b = g % NB; for (int k = 0; k < K; k++) dRb[g, k] = (SRb[g, k] + kReg * dNb[b, k]) / (WRb[g] + kReg); }
      var dUb = new double[nU * NB, K];
      for (int g = 0; g < nU * NB; g++) { int u = g / NB, b = g % NB, p = ufReg[u] * NB + b; for (int k = 0; k < K; k++) dUb[g, k] = (SUb[g, k] + kUF * dRb[p, k]) / (WUb[g] + kUF); }
      // ajuste do município: resíduo em relação ao efeito de perfil da UF
      var SM = new double[nM, K]; var WM = new double[nM];
      for (int i = 0; i < n; i++) {
        var s = secs[i]; int ui = s.uf_i * NB + bin[i];
        double w = s.V * (wMun == null ? 1 : wMun[s.mun] * wSec[i]);
        for (int k = 0; k < K; k++) SM[s.mun, k] += w * (s.y[k] - s.b[k] - dUb[ui, k]);
        WM[s.mun] += w;
      }
      // comparecimento: município -> UF -> região -> país
      double rN = VbN > 0 ? VN / VbN : 1;
      var rR = new double[nR]; for (int g = 0; g < nR; g++) rR[g] = (VR[g] + kReg * rN) / (VbR[g] + kReg);
      var rU = new double[nU]; for (int g = 0; g < nU; g++) rU[g] = (VU[g] + kUF * rR[ufReg[g]]) / (VbU[g] + kUF);
      var rM = new double[nM]; for (int g = 0; g < nM; g++) rM[g] = (VM[g] + kMun * rU[munUF[g]]) / (VbM[g] + kMun);
      var yh = new double[K];
      for (int i = n; i < N; i++) {
        var s = secs[i]; int ui = s.uf_i * NB + bin[i]; double sum = 0;
        for (int k = 0; k < K; k++) { yh[k] = Math.Max(0, Math.Min(1, s.b[k] + dUb[ui, k] + SM[s.mun, k] / (WM[s.mun] + kMun))); sum += yh[k]; }
        double Vh = s.Vb * rM[s.mun];
        for (int k = 0; k < K; k++) tot[k] += Vh * (sum > 0 ? yh[k] / sum : 1.0 / K);
      }
      double T = tot.Sum(); for (int k = 0; k < K; k++) tot[k] /= T;
      return tot;
    };

    var sb = new StringBuilder();
    sb.Append("{\"secoes\":").Append(N).Append(",\"comBase\":").Append(matched).Append(",\"semBase\":").Append(semBase);
    sb.Append(",\"final\":[").Append(string.Join(",", final.Select(v => (100 * v).ToString("0.000", IC)))).Append("]");
    sb.Append(",\"k\":[").Append(kMun.ToString(IC)).Append(",").Append(kUF.ToString(IC)).Append(",").Append(kReg.ToString(IC)).Append("]");

    // Curva real e projeção pontual a cada 0,5% das seções
    var curve = new List<string>(); var proj = new List<string>();
    var counted = new double[K]; double cv = 0; int next = 1;
    for (int i = 0; i < N; i++) {
      var s = secs[i]; for (int k = 0; k < K; k++) counted[k] += s.V * s.y[k]; cv += s.V;
      while (next <= 200 && (i + 1) >= (long)Math.Ceiling(N * next / 200.0)) {
        double pct = next * 0.5; int n = i + 1;
        string hora = DateTimeOffset.FromUnixTimeSeconds(s.t).UtcDateTime.ToString("HH:mm", IC);
        curve.Add("[" + pct.ToString("0.0", IC) + "," + string.Join(",", counted.Select(v => (100 * v / cv).ToString("0.000", IC))) + ",\"" + hora + "\"]");
        if (next < 200) { var p = Project(n, null, null); proj.Add("[" + pct.ToString("0.0", IC) + "," + string.Join(",", p.Select(v => (100 * v).ToString("0.000", IC))) + "]"); }
        next++;
      }
    }
    sb.Append(",\"curve\":[").Append(string.Join(",", curve)).Append("]");
    sb.Append(",\"proj\":[").Append(string.Join(",", proj)).Append("]");

    // Faixa de incerteza (bootstrap) a cada 2,5% das seções: percentis 5 e 95
    var rnd = new Random(seed); var band = new List<string>();
    Func<double> Exp1 = () => -Math.Log(1 - rnd.NextDouble());
    var wM = new double[nM]; var wS = new double[N];
    for (int step = 1; step < 40; step++) {
      double pct = step * 2.5; int n = (int)Math.Ceiling(N * pct / 100.0);
      var reps = new double[B][];
      for (int b = 0; b < B; b++) {
        for (int m = 0; m < nM; m++) wM[m] = Exp1();
        for (int i = 0; i < n; i++) wS[i] = Exp1();
        reps[b] = Project(n, wM, wS);
      }
      // Faixa = ruído amostral (bootstrap) + erro sistemático proporcional ao que falta apurar
      // (ordem de chegada das urnas), somados em quadratura. tau calibrado pelos erros observados nos testes.
      var center = Project(n, null, null);
      double sys = 1.645 * tau * (1 - pct / 100.0);
      var parts = new List<string>();
      for (int k = 0; k < K; k++) {
        var v = reps.Select(r => r[k]).OrderBy(x => x).ToArray();
        double q05 = v[(int)(0.05 * (B - 1))], q95 = v[(int)Math.Ceiling(0.95 * (B - 1))], c = center[k];
        double lo = c - Math.Sqrt(Math.Pow(Math.Max(0, c - q05), 2) + sys * sys), hi = c + Math.Sqrt(Math.Pow(Math.Max(0, q95 - c), 2) + sys * sys);
        parts.Add((100 * lo).ToString("0.000", IC)); parts.Add((100 * hi).ToString("0.000", IC));
      }
      band.Add("[" + pct.ToString("0.0", IC) + "," + string.Join(",", parts) + "]");
      
    }
    sb.Append(",\"band\":[").Append(string.Join(",", band)).Append("]}");
    return sb.ToString();
  }
}