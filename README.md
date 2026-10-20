# Clotch

Clotch adalah aplikasi system tray Windows untuk memantau usage **Claude Code / Claude subscription**.
Usage ditampilkan sebagai *notch* kecil di bagian atas layar.

- Muncul di system tray saat dijalankan.
- Mendeteksi **Claude Code** (npm / native) dan **Claude Desktop** (MSIX atau Squirrel lama), lalu mengecek apakah ada subscription aktif (Pro / Max / Team / Enterprise).
- Kalau ada, notch muncul di tengah atas layar dan menampilkan % limit sesi 5 jam dan % limit mingguan.
- Notch bisa **di-drag**, dan posisinya disimpan. **Klik** untuk melihat detail. Notch bisa **disembunyikan** lewat tombol ✕ atau menu tray.

## Menjalankan

```sh
# dari source (butuh .NET 10 SDK)
dotnet run --project src/Clotch.App -c Release

# build exe single-file self-contained (tidak perlu .NET di mesin target, ~170 MB)
dotnet publish src/Clotch.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/publish

# alternatif kecil (~350 KB), butuh .NET 10 Desktop Runtime terpasang
dotnet publish src/Clotch.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o artifacts/publish-fd
```

`EnableCompressionInSingleFile` sengaja tidak dipakai. Opsi itu memperkecil exe menjadi ~75 MB, tetapi pemakaian RAM naik dari ~49 MB menjadi ~170 MB.

Hanya satu instance yang berjalan. Kalau `Clotch.exe` dijalankan lagi, instance yang sudah berjalan akan menampilkan notch-nya.

## Cara pakai

| Aksi | Hasil |
|---|---|
| Klik baris notch | Expand / collapse detail |
| Drag notch | Pindah posisi. Posisi di-clamp ke layar dan disimpan |
| ✕ di notch / "Sembunyikan" | Notch disembunyikan. Status ini disimpan |
| Klik kiri icon tray | Tampilkan / sembunyikan notch. Kalau notch tidak tersedia, muncul penjelasannya |
| Klik kanan icon tray | Menu: refresh, buka halaman usage claude.ai, kembalikan posisi, interval refresh (2–30 menit), sembunyikan saat fullscreen, jalankan saat Windows start, folder log, keluar |

Isi panel detail:

- Plan dan status data (Live / cache).
- Semua limit yang dikirim server, lengkap dengan waktu reset dan countdown.
- Pembagian pemakaian mingguan per produk (Claude Code / Chats / Cowork / Other).
- Status extra usage.
- **Token lokal**: token Claude Code di mesin ini per model, untuk hari ini dan window 5 jam aktif. Datanya dibaca dari `~/.claude/projects/**/*.jsonl`.

Notch tidak muncul pada kondisi berikut. Penyebabnya ditampilkan di tooltip / balloon tray:

| Kondisi | Pesan |
|---|---|
| Claude Code dan Claude Desktop tidak terinstall | tidak terdeteksi |
| Hanya Claude Desktop, belum login Claude Code | login Claude Code diperlukan |
| Claude Code terinstall, belum login | jalankan `claude` lalu `/login` |
| Plan free / tidak diketahui | tidak ada subscription aktif |
| Token ditolak server dan belum ada data cache | login ulang |

## Sumber data & privasi

- **Kredensial**: `%USERPROFILE%\.claude\.credentials.json`, atau `CLAUDE_CONFIG_DIR` kalau di-set. File ini **hanya dibaca**. Clotch tidak pernah menulisnya dan tidak pernah me-refresh token. Refresh token tetap urusan Claude Code, jadi Clotch tidak bisa membuat sesi Claude Code logout. Kalau token sudah kedaluwarsa, Clotch berhenti memanggil API sampai Claude Code me-refresh tokennya.
- **Usage**: `GET https://api.anthropic.com/api/oauth/usage`, endpoint yang dipakai perintah `/usage` di Claude Code. Endpoint ini **tidak resmi** dan bisa berubah. Karena itu parser dibuat toleran: field yang hilang atau tidak dikenal tidak membuat app crash, dan key baru ikut ditampilkan dengan namanya apa adanya.
- **Rate limit**: Clotch memanggil endpoint 1× per 5 menit secara default (minimal 2 menit). Kalau gagal, jeda dinaikkan bertahap 10 → 20 → 30 menit (maksimum). `Retry-After` dari server dihormati. Refresh manual dibatasi 1× per menit. Data terakhir yang berhasil tetap ditampilkan dan ditandai "cache".
- **Network**: Clotch hanya menghubungi `api.anthropic.com`.
- **Log**: `%LOCALAPPDATA%\Clotch\logs`. Semua baris log melewati redaksi token, jadi tidak ada `sk-ant-…` maupun header `Authorization` yang tercatat.
- **Settings**: `%APPDATA%\Clotch\settings.json`.
- **Autostart**: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `Clotch`.

## Struktur

```
src/Clotch.Core   deteksi, kredensial, client + parser usage, monitor/backoff, scanner JSONL, gating, settings
src/Clotch.App    WPF: tray (WinForms NotifyIcon), NotchWindow, view model
src/Clotch.Diag   clotch-diag: dump JSON apa yang dilihat Clotch (token disensor)
tests/Clotch.Tests  xUnit
tools/make_icon.py  generator icon (stdlib Python)
```

## Verifikasi

```sh
dotnet build Clotch.slnx -c Release
dotnet test Clotch.slnx -c Release
dotnet run --project src/Clotch.Diag -c Release            # exit 0 = notch akan tampil, 2 = tidak
dotnet run --project src/Clotch.Diag -c Release -- --no-network
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v Clotch
grep -rE "sk-ant-|Bearer " "$LOCALAPPDATA/Clotch/logs"      # harus kosong
```

## Batasan yang diketahui

- Endpoint usage tidak terdokumentasi. Kalau Anthropic mengubahnya, panel limit akan kosong, tetapi token lokal tetap tampil.
- Beberapa key di respons server berupa nama kode (mis. `iguana_necktie`). Key seperti ini ditampilkan apa adanya, tanpa ditebak artinya.
- Unit `monthly_limit` / `used_credits` pada extra usage tidak terdokumentasi, jadi yang ditampilkan hanya persentase dari server.
- Pengguna yang hanya memakai Claude Desktop (tanpa login Claude Code) tidak bisa dibaca usage-nya. Ini disengaja: Clotch tidak membongkar cookie Claude Desktop.
