# Rencana: antarmuka top bar (sidebar kanan dihapus)

Status: T1–T4 dikerjakan di `custom` (2026-10-28, versi 1.2.0-custom.2; tes otomatis hijau, tampilan dicek lewat gambar `--selftest shot`). Belum diuji: seret, ubah ukuran, maksimalkan dengan mouse, snap layouts, DPI 125/150%, tema kontras tinggi, teks terpotong di lebar minimum. Dulu: rencana, belum dikerjakan. Berlaku global dan akan digabung ke `main`. Belum ada kode yang diubah dan belum diuji. Mockup interaktif: https://claude.ai/artifact/7nzyWz58G61L9SVPsSBdPW (privat).

## Keputusan pemilik (2026-10-27)
- Sidebar kanan (Render, Layar penuh, NDI, OMT, Audio, Statistik) dihapus; area pratinjau memakai seluruh lebar di kanan daftar tab.
- NDI dan OMT menjadi tombol di top bar yang menampilkan statusnya. Klik membuka jendela: nyala/mati, nama sumber, opsi "Kirim alpha" (bawaan mati). Saat test pattern aktif, tombol menunjukkan "ON · TEST" (lihat `docs/PLAN_EMPTY_STAGE.md`).
- Resolusi dan FPS: satu tombol di kanan atas (misalnya "2520 × 1080 · 30 fps") membuka jendela Render: preset, lebar × tinggi, 30/60 fps, Terapkan, Simpan preset, Kelola.
- Audio: tidak ada tombol sendiri. Meter L/R di kanan tombol Statistik; klik meter membuka jendela Audio (perangkat lokal). Nama perangkat hanya tooltip.
- Bingkai jendela menyatu: garis judul Windows dihapus; tombol perkecil/perbesar/tutup ada di ujung kanan top bar. Area kosong top bar untuk menggeser jendela, klik dua kali memperbesar, tepi tetap bisa ditarik.

## Asumsi saya (belum dikonfirmasi)
- Layar penuh menjadi tombol dengan jendela sendiri; Statistik menjadi jendela kecil dari tombol di top bar.
- Meter mengukur level yang dikirim ke NDI/OMT (dari `AudioHub`), bukan level perangkat lokal.
- Perubahan nama sumber saat output menyala: ditentukan saat dibuat.
- Pilihan susunan jendela Windows 11 (snap layouts) di tombol perbesar: belum diketahui, harus dicoba.
- Perilaku di tema kontras tinggi dengan bingkai khusus: harus diuji.

## Rencana eksekusi (nanti)

Aturan proyek: typecheck selama membangun, batch pengujian penuh hanya saat diminta, tidak ada fase ditandai lulus sebelum diuji. Kontrol yang ada sekarang dipindah, bukan ditulis ulang, dari `MainWindow.Output.cs`, `MainWindow.Ndi.cs`, `MainWindow.Omt.cs`, dan `MainWindow.Audio.cs`.

### T1 - Kerangka top bar dan jendela pengaturan
- `MainWindow.xaml`: hapus `SideColumn`/`SideScroll`; top bar berisi merek, pil status, tombol Layar/NDI/OMT/Statistik, meter audio, tombol ukuran render; kolom tengah memakai lebar penuh.
- Komponen jendela pengaturan di dalam jendela (lapisan di atas pratinjau dengan latar redup, Esc menutup, fokus kembali ke tombol pemanggil), dipakai bersama oleh semua jendela.
- `App.xaml`: gaya baru (tombol chip, saklar, kolom isian) mengikuti tema yang ada.
- Lulus kalau: semua kontrol yang tadinya di sidebar bisa dijangkau lewat top bar dan berfungsi sama.

### T2 - Isi jendela
- Jendela NDI dan OMT: nyala/mati, nama sumber, alpha (mengikuti `docs/PLAN_PPTX.md` fase P2b; sampai saat itu alpha disembunyikan atau dinonaktifkan).
- Jendela Layar: monitor, nyala/mati. Jendela Audio: perangkat lokal, nada uji yang sudah ada. Jendela Render: preset, W×H, FPS. Jendela Statistik: isi panel yang sekarang.
- Status di tombol (MATI/ON) mengikuti `RefreshHeader()` yang sudah ada.
- Lulus kalau: perilaku tiap kontrol sama dengan sebelumnya (nyala manual, tidak ada penyimpanan status nyala).

### T3 - Meter audio di top bar
- Meter L/R memakai `LevelMeter` dari `AudioHub`; perbarui pada timer yang sudah ada, tanpa mengganggu UI saat sunyi.

### T4 - Bingkai jendela menyatu
- `WindowChrome` (tinggi caption = tinggi top bar, tepi ubah-ukuran tetap, tanpa frame kaca), tombol sistem digambar sendiri dengan `IsHitTestVisibleInChrome`.
- Perbaikan saat dimaksimalkan (WPF meluap beberapa piksel di tepi layar), klik dua kali dan seret pada area kosong, tombol tutup merah saat disorot.
- Snap layouts Windows 11: dicoba dengan penanganan `WM_NCHITTEST` (HTMAXBUTTON); jika tidak berhasil, dicatat sebagai batas yang diketahui.
- Lulus kalau: geser, ubah ukuran, maksimalkan, kembalikan, dan Win+panah bekerja; diuji pada DPI 100/125/150%.

### T5 - Pengujian dan rilis
- `SelfTest.cs` yang menyentuh kontrol sidebar disesuaikan dengan lokasi baru; gambar layar acuan (`shot.ps1`) diperbarui.
- Cek teks terpotong pada lebar jendela minimum (1220 px) dan skala DPI.
- Naikkan versi, tulis `CHANGELOG.md`, gabung ke `main` setelah lulus.

### Urutan
T1 → T2 → T3 → T4 → T5. T4 independen dan bisa dikerjakan terpisah.
