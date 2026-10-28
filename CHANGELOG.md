# Changelog

Format mengikuti [Keep a Changelog](https://keepachangelog.com/); versi mengikuti `<Version>` di
`src/Stagecast.App/Stagecast.App.csproj` (sumber tunggal untuk exe, zip, dan installer).

## [1.2.0-custom.2] - belum dirilis

Versi percobaan dari branch `custom`; bukan rilis resmi dan belum digabung ke `main`. Menambahkan sumber PowerPoint di atas
isi `1.2.0-custom.1` (PDF dan clicker, di bawah).

### Ditambahkan
- Sumber PowerPoint: tab PPT (tombol **+ PPT**) memutar berkas `.pptx` lewat PowerPoint yang terpasang (otomasi COM). Slide show
  layar penuh dijalankan di luar layar lalu ditangkap, jadi tidak ada jendela tambahan; animasi, transisi, dan video adalah milik
  PowerPoint. Scale to fit dengan bar hitam. Slide maju hanya saat diperintah (waktu di berkas diabaikan); slide tersembunyi
  dilewati, dan meloncat ke slide tersembunyi meminta konfirmasi. Next di slide terakhir tidak menutup show.
  Kontrol halaman, strip thumbnail (slide tersembunyi ditandai TERSEMBUNYI), Muat ulang, Pilih berkas, dan clicker memakai
  antarmuka dokumen yang sama dengan PDF. Berkas dibuka sebagai salinan sementara hanya-baca, makro dimatikan.
- PowerPoint milik operator tidak pernah ditutup: hanya presentasi salinan milik Stagecast yang ditutup. PowerPoint yang dimulai
  Stagecast dipakai bersama semua tab PPT dan ditutup oleh tab terakhir.
- Suara dan video di dalam slide: suara proses PowerPoint diteruskan ke output hanya saat tab PPT tayang; Freeze membisukannya.
- Preview + Take untuk tab PPT: deck disiapkan di preview, Take tidak memulai ulang show, dua deck bisa berjalan bersamaan.
  Bar PROGRAM meminta konfirmasi sebelum menayangkan slide tersembunyi.
- Opsi **Kirim alpha (bar tembus pandang)** di kartu NDI dan OMT (mati secara bawaan, terpisah, tersimpan). Bar di sekitar PDF atau
  PPT dikirim transparan; Blackout tetap hitam pekat, output layar tetap hitam.
- Peringatan di tab PPT bila font tidak terpasang dan tidak disertakan di berkas, atau media tertaut tidak ditemukan.
- Ketahanan: PowerPoint yang mati di tengah show dilaporkan dalam sekitar 2 detik, Muat ulang menyalakannya lagi.
- Self test `p2`, `p1`, `p4`, `p5` (masuk smoke), `p3` (deck asli operator, manual); spike di `spikes/Stagecast.PptxSpike`, hasil di
  `docs/SPIKE_REPORT.md`.

- Pengaman: selama ada output yang menyala (Layar, NDI, OMT), Windows ditahan agar tidak tidur dan layar tidak mati (permintaan daya
  dilepas saat semua output mati atau aplikasi ditutup); menutup Stagecast saat output menyala meminta konfirmasi yang menyebut unit
  yang menyala (Windows mati atau logoff dan uji otomatis tidak ditanya). Tidak mencegah kunci layar dari kebijakan, Win+L, atau tutup laptop.
- Strip thumbnail di bar PROGRAM: deck atau PDF yang tayang tetap punya strip yang bisa diklik saat preview menampilkan tab lain.

- Test pattern: bar warna, gradasi abu-abu, petak, lingkaran dan garis silang, bingkai tepi, serta bagian bergerak (timecode, bar yang
  menyapu, kotak BEEP) yang dihitung dari ukuran render. Tiap unit (Layar, NDI, OMT) punya saklar sendiri, tidak tersimpan; unit
  yang memintanya menampilkan pola sementara unit lain tetap live. Blackout menang atas pola dan Freeze tidak membekukannya. Suara
  pola berupa burst 1 kHz (-20 dBFS) 100 ms tiap detik pada jam yang sama dengan kotak BEEP.
- Saat aplikasi dibuka tidak ada tab yang tayang: output (bila dinyalakan) menampilkan pola ("POLA UJI" di status) sampai operator
  memilih tab (mode Langsung) atau menekan TAKE (Preview + Take).

- Tab boleh nol: semua tab, termasuk yang terakhir dan yang sedang tayang, bisa ditutup. Menutup tab yang tayang saat output menyala
  meminta konfirmasi, lalu output menampilkan test pattern; di Preview + Take, menutup tab program mengakhiri mode itu dan tab
  preview tetap terpilih. Pertama kali dibuka tidak ada tab. Daftar tab kosong menampilkan petunjuk, dan bilah alamat dimatikan.

### Diubah
- Antarmuka top bar: sidebar kanan dihapus dan area pratinjau memakai seluruh lebar di kanan daftar tab. Layar, NDI, OMT, Audio,
  Statistik, dan Render kini jendela pengaturan di dalam aplikasi (lapisan di atas jendela; Esc atau klik di luar menutup) yang dibuka
  dari tombol di top bar. Tombol Layar/NDI/OMT menampilkan status (mati, nyala, "nyala α" bila kirim alpha, "nyala · TEST" saat pola
  uji), meter audio L/R ada di top bar dan membuka jendela Audio, tombol ukuran render ("2520 × 1080 · 60 fps") membuka jendela Render.
  Bingkai jendela menyatu: garis judul Windows dihapus; perkecil/perbesar/tutup ada di ujung kanan top bar, area kosong top bar untuk
  menggeser dan klik dua kali memperbesar. Tombol **Perbesar** kini menyembunyikan daftar tab. Susunan jendela Windows 11 (snap
  layouts) di tombol perbesar dan tema kontras tinggi belum diuji.

### Diperbaiki
- PDF: gambar usang setelah resolusi render diganti (halaman digambar ulang bersamaan dengan capture dimulai ulang).

### Belum diuji
Clicker asli, dua monitor dan DPI 150%, kilatan jendela show sekitar 1 detik saat deck mulai, suara PowerPoint ke speaker lokal saat
tab tidak tayang, penerimaan alpha oleh vMix/Resolume dan tampilan pratinjaunya, dialog tak terduga PowerPoint (Protected View,
pemulihan, kata sandi), deck sangat panjang, soak. Konfirmasi tutup dan penahan tidur selama sesi panjang belum diuji manual (`powercfg /requests`).

## [1.2.0-custom.1] - belum dirilis

Versi percobaan dari branch `custom`; bukan rilis resmi dan belum digabung ke `main`.

### Ditambahkan
- Sumber PDF: tab PDF (tombol **+ PDF**) menampilkan halaman dengan scale to fit di layar penuh, NDI, dan OMT; bar hitam di sisa kanvas.
  Kontrol halaman (awal, sebelumnya, berikutnya, akhir, kotak nomor halaman), strip thumbnail dengan bingkai merah (sekarang) dan biru
  (berikutnya), Muat ulang dan Pilih berkas dengan konfirmasi bila tab sedang tayang, kata sandi PDF ditanyakan di dalam aplikasi
  dan tidak disimpan. Berkas dibuka dari salinan sementara.
- Clicker presentasi (Page Down/Up, panah, Backspace, B atau titik untuk Blackout, F5 kembali ke halaman 1); mode global opsional
  untuk tombol halaman saja. Di Preview + Take, halaman tab yang sedang tayang tetap bisa digeser lewat bar PROGRAM atau clicker.
- Jenis tab (`Web`, `Pdf`, kelak `PowerPoint`, `Video`) di daftar tab dan berkas pengaturan; pengaturan lama dibaca sebagai tab web.
- `scripts/check.sh selftest NAMA`, `CONTINUE=1` untuk smoke, dan self test `d1a` dan `d5`.

### Belum diuji
Tampilan UI (strip, DPI 125/150%), kata sandi, rotasi dan ukuran halaman campuran, PDF ratusan halaman, soak dengan output menyala,
dan alpha untuk bar transparan (dibangun di `1.2.0-custom.2`).

## [1.1.6] - 2026-10-27

Rilis publik pertama. Isinya sama dengan 0.1.6 (tidak ada perubahan kode); hanya nomor versi yang dinaikkan. Riwayat 0.1.0
sampai 0.1.6 di bawah adalah versi pengembangan sebelum rilis ini.

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
