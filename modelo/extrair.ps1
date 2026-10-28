# Baixa os boletins de urna (BU) do TSE e resume por seção eleitoral (cargo Presidente).
# Saída: um TSV por eleição com uma linha por seção:
# uf, municipio, zona, secao, epoch_recebido, aptos, comparecimento, validos, v13, v22
param([string]$Work, [string]$Ano, [string]$Turno, [string]$Stamp, [string]$OutFile)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
if (-not ('BUx' -as [type])) {
Add-Type -ReferencedAssemblies System.IO.Compression, System.IO.Compression.FileSystem -TypeDefinition @'
using System; using System.IO; using System.IO.Compression; using System.Collections.Generic; using System.Text; using System.Globalization;
public class SecX { public string uf, mun, zona, secao; public long t = long.MaxValue; public long aptos, comp, valid, v13, v22; public bool seen; }
public static class BUx {
  static int idx(string[] h, string n) { int i = Array.IndexOf(h, "\"" + n + "\""); if (i < 0) throw new Exception("coluna " + n); return i; }
  public static int Append(string zipPath, StreamWriter w) {
    var d = new Dictionary<string, SecX>();
    using (var z = ZipFile.OpenRead(zipPath)) {
      foreach (var e in z.Entries) {
        if (!e.Name.EndsWith(".csv")) continue;
        using (var sr = new StreamReader(e.Open(), Encoding.GetEncoding(1252))) {
          var h = sr.ReadLine().Split(';');
          int iUF = idx(h,"SG_UF"), iMun = idx(h,"CD_MUNICIPIO"), iZ = idx(h,"NR_ZONA"), iS = idx(h,"NR_SECAO"),
              iC = idx(h,"CD_CARGO_PERGUNTA"), iT = idx(h,"DT_BU_RECEBIDO"), iA = idx(h,"QT_APTOS"), iCo = idx(h,"QT_COMPARECIMENTO"),
              iTV = idx(h,"DS_TIPO_VOTAVEL"), iN = idx(h,"NR_VOTAVEL"), iV = idx(h,"QT_VOTOS");
          string line;
          string[] fmts = { "dd/MM/yyyy HH:mm:ss", "yyyy-MM-dd HH:mm:ss" };
          while ((line = sr.ReadLine()) != null) {
            var c = line.Split(';');
            if (c[iC].Trim('"') != "1") continue;
            string uf = c[iUF].Trim('"'), mun = int.Parse(c[iMun].Trim('"')).ToString(), zo = int.Parse(c[iZ].Trim('"')).ToString(), se = int.Parse(c[iS].Trim('"')).ToString();
            string key = uf + "|" + mun + "|" + zo + "|" + se;
            SecX s;
            if (!d.TryGetValue(key, out s)) {
              s = new SecX { uf = uf, mun = mun, zona = zo, secao = se };
              s.aptos = long.Parse(c[iA].Trim('"')); s.comp = long.Parse(c[iCo].Trim('"'));
              d[key] = s;
            }
            DateTime dt;
            if (DateTime.TryParseExact(c[iT].Trim('"'), fmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) {
              long ep = (long)(dt - new DateTime(1970,1,1)).TotalSeconds; if (ep < s.t) s.t = ep;
            }
            if (c[iTV].Trim('"') != "Nominal") continue;
            long v = long.Parse(c[iV].Trim('"')); string nr = c[iN].Trim('"');
            s.valid += v; if (nr == "13") s.v13 += v; else if (nr == "22") s.v22 += v;
          }
        }
      }
    }
    foreach (var s in d.Values)
      w.WriteLine(string.Join("\t", s.uf, s.mun, s.zona, s.secao, s.t == long.MaxValue ? 0 : s.t, s.aptos, s.comp, s.valid, s.v13, s.v22));
    return d.Count;
  }
}
'@
}
$ufs = 'AC','AL','AM','AP','BA','CE','DF','ES','GO','MA','MG','MS','MT','PA','PB','PE','PI','PR','RJ','RN','RO','RR','RS','SC','SE','SP','TO','ZZ'
$tmp = Join-Path $Work "zip_${Ano}_$Turno"
New-Item -ItemType Directory -Force $tmp | Out-Null
$w = New-Object IO.StreamWriter -ArgumentList ($OutFile + '.part'), $false, (New-Object Text.UTF8Encoding $false)
$total = 0
foreach ($uf in $ufs) {
  $zip = Join-Path $tmp "$uf.zip"
  $url = "https://cdn.tse.jus.br/estatistica/sead/eleicoes/eleicoes$Ano/buweb/bweb_${Turno}t_${uf}_$Stamp.zip"
  for ($k = 1; $k -le 6; $k++) {
    try { Invoke-WebRequest $url -OutFile $zip; break }
    catch { if ($k -eq 6) { throw }; Write-Output "  falha ao baixar $uf ($($_.Exception.Message)), tentando de novo"; Start-Sleep (20 * $k) }
  }
  $n = [BUx]::Append($zip, $w); $total += $n
  Remove-Item $zip
  Write-Output "$Ano $($Turno)T $uf : $n secoes"
}
$w.Close()
Move-Item ($OutFile + '.part') $OutFile -Force
Write-Output "OK $Ano $($Turno)T : $total secoes -> $OutFile"
