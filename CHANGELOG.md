# Changelog

Format mengikuti [Keep a Changelog](https://keepachangelog.com/); versi mengikuti `<Version>` di
`src/Stagecast.App/Stagecast.App.csproj` (sumber tunggal untuk exe, zip, dan installer).

## [0.1.4] - 2026-10-27

### Diubah
- Installer sekarang memasang **NDI Runtime** secara otomatis bila belum terpasang (sebelumnya hanya opsi unduh NDI Tools
  540 MB yang tidak dicentang). Yang diunduh adalah installer runtime-saja resmi dari ndi.video (sekitar 10 MB), diperiksa
  tanda tangan digital Vizrt-nya sebelum dijalankan, lalu dipasang senyap. Bila runtime sudah ada, langkah ini dilewati.
  Bila unduhan atau pemasangan gagal, pengguna ditanya mau lanjut tanpa NDI (layar dan OMT tetap bisa dipakai).
- Saklar uji installer `/SimulateNdiMissing=1`.

## [0.1.3] - 2026-10-27

### Diperbaiki
- Tombol "Preview + Take" tetap nonaktif walau sudah ada dua tab atau lebih. Status tombol sekarang diperbarui setiap kali
  tab ditambah atau ditutup. Cara pakai: buka minimal dua tab, tab yang sedang tayang tetap jalan, klik "Preview + Take",
  pilih tab lain untuk disiapkan, lalu klik TAKE (atau tekan Spasi) untuk menayangkannya.

## [0.1.2] - 2026-10-27

### Diperbaiki
- Panel samping terpotong di sisi kanan (tombol "Kelola" dan tombol "Ganti nama" pada NDI/OMT). Lebar panel dinaikkan
  dari 300 ke 340 dan baris tombol resolusi (Terapkan, Simpan preset, Kelola) sekarang turun ke baris berikutnya bila
  tidak muat.

## [0.1.1] - 2026-10-27

### Diperbaiki
- Dialog "Value does not fall within the expected range" saat mouse digerakkan di atas pratinjau. WebView2 kadang menolak
  input mouse (`E_INVALIDARG`) pada kondisi sesaat; sekarang input yang ditolak diabaikan dan dicatat sekali di log
  (`SendMouse` di `RenderEngine`). Akar masalahnya belum dipastikan, perbaikan ini mencegah aplikasi menampilkan error.

## [0.1.0] - 2026-10-27

### Ditambahkan
- Render halaman web ke resolusi kustom (WebView2), pratinjau dengan Preview + Take, multi-tab dengan profil login
  bersama/terpisah.
- Keluaran layar penuh ke monitor pilihan dengan squeeze, NDI, dan OMT (vMix), audio dengan pilihan perangkat lokal,
  Freeze/Blackout, FPS 30/60.
- Pemulihan otomatis (jaringan, render crash, proses browser berhenti).
- Paket portabel (zip) dan installer Inno Setup dengan Start Menu, cek prasyarat, unduh WebView2 otomatis, dan opsi
  unduh NDI Tools.
- Logo "S".
