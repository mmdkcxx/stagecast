# Changelog

Format mengikuti [Keep a Changelog](https://keepachangelog.com/); versi mengikuti `<Version>` di
`src/Stagecast.App/Stagecast.App.csproj` (sumber tunggal untuk exe, zip, dan installer).

## [0.1.6] - 2026-10-27

### Ditambahkan
- Thumbnail tab di daftar tab (96 × 54): gambar terakhir tab itu saat tampil di preview atau tayang, diperbarui tiap 1,5 detik
  selama tab tampil. Tab yang belum pernah tampil menampilkan nama situsnya. Saat preview diperbesar ke 100% (hanya sebagian
  halaman terlihat) thumbnail tidak diperbarui.
- Outline tally di sekeliling preview: merah saat tab yang dilihat sedang tayang di output, kuning saat FREEZE, putih saat
  BLACKOUT, hijau untuk tab preview di mode Preview + Take, dan abu-abu bila tidak ada output yang menyala.

### Diubah
- Kolom tab melebar menjadi 320 px, tombol ganti nama dan tutup ditumpuk vertikal di sisi kanan baris tab; lebar minimum
  jendela 1220 px.

## [0.1.5] - 2026-10-27

### Diubah
- Antarmuka disesuaikan dengan rancangan di artifact "Stagecast UI": tiga kolom (tab di kiri, preview di tengah, pengaturan
  di kanan), bar atas dengan status LIVE / FREEZE / BLACKOUT, chip LAYAR / NDI / OMT, dan keterangan resolusi + fps.
- Tombol TAKE, BLACKOUT, dan FREEZE sekarang besar dan berada tepat di bawah preview (sebelumnya di panel samping).
- Kolom alamat dan tombol kembali/maju/muat ulang pindah ke atas preview.
- Font Barlow dan IBM Plex Mono (lisensi OFL) disertakan; kontrol bergaya baru: tombol segmen (Langsung / Preview + Take,
  Fit / 100%, FPS, Peras / Fit / 1:1), kolom isian, kotak centang, slider, scrollbar, tooltip, dan menu klik kanan yang gelap.
- Baris tab dua baris dengan chip status (LIVE, PREVIEW, PROFIL, SESI, MASALAH). Tombol naik/turun pindah ke menu klik kanan.
- Tombol nyalakan Output/NDI/OMT tampil sebagai tombol sekunder agar tidak terlalu mencolok.
- Lebar minimum jendela naik menjadi 1180 px; panel kanan 360 px supaya teks tombol tidak terpotong.
- Teks memakai mode tampilan yang dipetakan ke piksel (lebih rata dan jelas pada DPI 100%), ukuran teks kecil dinaikkan.
- Uji A2b: toleransi klik pada tampilan Fit mengikuti skala preview (satu piksel preview = beberapa piksel halaman).

### Belum ada dibanding rancangan
- Thumbnail tab, outline tally (ditambahkan di 0.1.6) dan bagian Output yang bisa dilipat.

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
