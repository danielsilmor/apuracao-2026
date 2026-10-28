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
  public double tb = double.NaN, ta;   // horário de chegada (segundos desde a 0h do dia da eleição): base e alvo
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

    // Dia de cada eleição (0h), para medir horários de chegada no mesmo relógio
    Func<IEnumerable<long>, long> DayStart = ts => { var v = ts.Where(x => x > 0).OrderBy(x => x).ToArray(); return v.Length == 0 ? 0 : (v[v.Length / 2] / 86400) * 86400; };
    long baseDay = DayStart(bas.Select(r => r.t)), tgtDay = DayStart(tgt.Select(r => r.t));

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
        if (br.t > 0) s.tb = br.t - baseDay;
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

    // Horário de chegada na base para seções sem correspondência: média do município, senão da UF
    {
      var mS = new double[nM]; var mC = new double[nM]; var uS = new double[nU]; var uC = new double[nU];
      foreach (var s in secs) { s.ta = s.t - tgtDay; if (!double.IsNaN(s.tb)) { mS[s.mun] += s.tb; mC[s.mun]++; uS[s.uf_i] += s.tb; uC[s.uf_i]++; } }
      double all = secs.Where(s => !double.IsNaN(s.tb)).Select(s => s.tb).DefaultIfEmpty(0).Average();
      foreach (var s in secs) if (double.IsNaN(s.tb)) s.tb = mC[s.mun] > 0 ? mS[s.mun] / mC[s.mun] : uC[s.uf_i] > 0 ? uS[s.uf_i] / uC[s.uf_i] : all;
    }

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
    double[] capY = null, capV = null;   // quando preenchidos, guardam a previsão de cada seção não apurada
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
        if (capY != null) { for (int k = 0; k < K; k++) capY[i * K + k] = sum > 0 ? yh[k] / sum : 1.0 / K; capV[i] = Vh; }
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
    sb.Append(",\"band\":[").Append(string.Join(",", band)).Append("]");

    // Trajetória prevista: as seções que faltam chegam na ordem em que a mesma seção chegou na eleição de base.
    // O horário previsto é o horário da base + o atraso típico observado nas últimas urnas apuradas.
    var traj = new List<string>();
    Func<double, string> Hora = sec => { long x = (long)Math.Round(sec); x = ((x % 86400) + 86400) % 86400; return (x / 3600).ToString("00") + ":" + (x % 3600 / 60).ToString("00"); };
    for (int cpi = 1; cpi <= 19; cpi++) {
      double cp = cpi * 5; int n = (int)Math.Ceiling(N * cp / 100.0);
      capY = new double[N * K]; capV = new double[N];
      Project(n, null, null);
      var cy = capY; var cvv = capV; capY = null; capV = null;
      // Ordem prevista das que faltam: cada UF continua do ponto em que está agora no "relógio" da base.
      // tauU = horário da base em que a UF tinha a mesma fração apurada que tem agora;
      // a seção chega em  agora + (horário na base − tauU) × ritmo, ritmo = velocidade desta noite / velocidade da base.
      double now = secs[n - 1].ta;
      var cntU = new int[nU]; var totU = new int[nU];
      for (int i = 0; i < N; i++) { totU[secs[i].uf_i]++; if (i < n) cntU[secs[i].uf_i]++; }
      var tbU = new List<double>[nU]; for (int u = 0; u < nU; u++) tbU[u] = new List<double>();
      foreach (var s in secs) tbU[s.uf_i].Add(s.tb);
      var tauU = new double[nU];
      for (int u = 0; u < nU; u++) { tbU[u].Sort(); int q = Math.Min(tbU[u].Count - 1, Math.Max(0, (int)((long)cntU[u] * tbU[u].Count / Math.Max(1, totU[u])))); tauU[u] = tbU[u].Count > 0 ? tbU[u][q] : 0; }
      var tbAll = secs.Select(s => s.tb).OrderBy(x => x).ToArray(); var taCnt = secs.Take(n).Select(s => s.ta).OrderBy(x => x).ToArray();
      // ritmo medido na metade mais recente do que já chegou (ignora as urnas do exterior que chegam horas antes)
      double a0 = secs[n / 2].ta, b0 = tbAll[n / 2], bNow = tbAll[Math.Min(N - 1, n)];
      double ritmo = (bNow - b0) > 60 ? Math.Max(0.3, Math.Min(3, (now - a0) / (bNow - b0))) : 1;
      var pred = new double[N];
      for (int i = n; i < N; i++) pred[i] = now + Math.Max(0, secs[i].tb - tauU[secs[i].uf_i]) * ritmo;
      double off = (ritmo - 1) * 100;   // relatório: % mais rápido/lento que a base
      var rest = Enumerable.Range(n, N - n).OrderBy(i => pred[i]).ThenBy(i => secs[i].tb).ThenBy(i => secs[i].key, StringComparer.Ordinal).ToArray();
      var acc = new double[K]; for (int i = 0; i < n; i++) for (int k = 0; k < K; k++) acc[k] += secs[i].V * secs[i].y[k];
      Func<int> Leader = () => acc[0] >= acc[1] ? 0 : 1;
      int lead = Leader(); var pts = new List<string>(); var vir = new List<double[]>(); var virH = new List<string>(); int lead0 = lead; int nextPct = (int)cp + 1;
      for (int j = 0; j < rest.Length; j++) {
        int i = rest[j]; for (int k = 0; k < K; k++) acc[k] += cvv[i] * cy[i * K + k];
        int cnt = n + j + 1; double pc = 100.0 * cnt / N; double tt = pred[i];
        int l = Leader();
        if (l != lead) { vir.Add(new double[] { pc, l }); virH.Add(Hora(tt)); lead = l; }
        if (pc >= nextPct || j == rest.Length - 1) {
          double T = acc.Sum();
          pts.Add("[" + pc.ToString("0.0", IC) + "," + string.Join(",", acc.Select(v => (100 * v / T).ToString("0.000", IC))) + ",\"" + Hora(tt) + "\"]");
          while (nextPct <= pc) nextPct++;
        }
      }
      traj.Add("{\"cp\":" + cp.ToString(IC) + ",\"ritmo\":" + Math.Round(off).ToString(IC) + ",\"pts\":[" + string.Join(",", pts) + "],\"viradas\":[" + Viradas(vir, virH, lead0) + "]}");
    }
    // Viradas reais da contagem
    var virR = new List<double[]>(); var virRH = new List<string>(); int ld0 = -1; { var a = new double[K]; int ld = -1;
      for (int i = 0; i < N; i++) { for (int k = 0; k < K; k++) a[k] += secs[i].V * secs[i].y[k]; int l = a[0] >= a[1] ? 0 : 1;
        if (ld >= 0 && l != ld) { virR.Add(new double[] { 100.0 * (i + 1) / N, l }); virRH.Add(Hora(secs[i].ta)); } if (ld < 0) ld0 = l; ld = l; } }
    sb.Append(",\"traj\":[").Append(string.Join(",", traj)).Append("]");
    sb.Append(",\"viradasReais\":[").Append(Viradas(virR, virRH, ld0)).Append("]}");
    return sb.ToString();
  }

  // Junta oscilações de liderança muito próximas (menos de 0,5% das seções) numa virada só
  static string Viradas(List<double[]> v, List<string> h, int lead0) {
    var o = new List<string>(); int lead = lead0, i = 0;
    while (i < v.Count) {
      int j = i; while (j + 1 < v.Count && v[j + 1][0] - v[j][0] < 0.5) j++;
      int after = (int)v[j][1];
      if (after != lead) { o.Add("[" + v[i][0].ToString("0.00", IC) + ",\"" + h[i] + "\"," + after + "]"); lead = after; }
      i = j + 1;
    }
    return string.Join(",", o);
  }
}