# Rencana: penahan tidur dan konfirmasi keluar

Status: dikerjakan di `custom` (2026-10-28): tahan tidur lewat permintaan daya Windows dan konfirmasi tutup. Konfirmasi tutup tidak bisa diuji otomatis (dialog). Dulu: rencana, belum dikerjakan. Berlaku global untuk seluruh aplikasi (bukan hanya sumber PowerPoint). Dikerjakan hanya di branch `custom`; tidak digabung atau diubah di `main` sampai pemilik meminta. Belum ada kode yang diubah dan belum diuji.

## Keputusan pemilik (2026-10-27)
1. Selama ada output yang menyala, PC tidak boleh tidur dan layar tidak boleh mati.
2. Menutup aplikasi saat ada output yang menyala meminta konfirmasi di dalam aplikasi.

## Keadaan sekarang (dari kode)
- Tidak ditemukan pemanggilan penahan tidur (`SetThreadExecutionState` atau sejenisnya) di `src/`.
- `MainWindow` hanya punya penutup `Closed` (menghentikan output, audio, dan mesin render, `MainWindow.xaml.cs`), tanpa `Closing`; `App.OnExit` hanya membereskan sisa.
- Sudah ada properti `MainWindow.OutputsLive` (`MainWindow.xaml.cs`) yang bernilai benar saat layar penuh, NDI, atau OMT menyala (atau simulasi uji menyala). Itu dipakai kembali, tidak ditulis ulang.
- Tombol tutup akan pindah ke top bar (`docs/PLAN_TOPBAR_UI.md` T4), jadi risiko tertutup tidak sengaja naik.

## Rancangan

### G1 - Tahan tidur
- Saat `OutputsLive` bernilai benar: minta Windows menahan layar dan sistem tetap hidup. Saat salah: lepas permintaan.
- Cara yang diusulkan: `PowerCreateRequest` + `PowerSetRequest` untuk `PowerRequestDisplayRequired` dan `PowerRequestSystemRequired` (satu handle, dilepas dengan jelas). Alternatif: `SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED)` yang harus dipanggil dari utas yang sama setiap kali. Pilihan akhir saat dikerjakan.
- Diperbarui dari pewaktu `RefreshHeader()` (250 ms) hanya saat keadaan berubah, bukan tiap putaran.
- Dilepas saat aplikasi menutup, saat output mati semua, dan saat proses berhenti paksa (Windows melepaskan permintaan sendiri).
- Ditulis ke log saat permintaan dibuat dan dilepas, untuk memudahkan penelusuran kalau PC tetap tidur.
- Batas yang diketahui: ini tidak mencegah kunci layar dari kebijakan perusahaan, Win+L, atau menutup tutup laptop. Itu dicatat di dokumentasi untuk operator.
- Lulus kalau: dengan output menyala, `powercfg /requests` menampilkan permintaan dari Stagecast; saat semua output mati atau aplikasi ditutup, permintaan hilang; PC tidak tidur setelah melewati batas waktu tidur yang diset pendek untuk uji.

### G2 - Konfirmasi saat menutup
- Tambah penanganan `Closing` di `MainWindow`: kalau `OutputsLive`, tampilkan konfirmasi di dalam aplikasi (gaya sama dengan dialog yang ada, mis. `Confirm(...)`), misalnya "Output sedang menyala (Layar, NDI). Tutup Stagecast dan hentikan semuanya?" dengan tombol Tutup dan Batal. Batal membatalkan penutupan.
- Teks menyebut unit yang sedang menyala.
- Tidak boleh menghalangi Windows mati atau logoff: pada `SessionEnding` konfirmasi dilewati dan aplikasi menutup dengan rapi.
- Tidak boleh mengganggu uji otomatis: self test dan penutupan lewat kode melewati konfirmasi (flag internal), supaya batch `check.sh` tidak menggantung.
- Penutupan dari tombol sistem (Alt+F4, tombol tutup di top bar, menu taskbar) semuanya lewat jalur `Closing` yang sama.
- Lulus kalau: dengan output menyala, tombol tutup menampilkan konfirmasi dan Batal menjaga output tetap menyala; tanpa output, aplikasi menutup tanpa pertanyaan; uji otomatis tidak menggantung.

## Urutan dan catatan
- G1 dan G2 kecil dan saling bebas; keduanya tidak bergantung pada top bar, sumber PowerPoint, atau test pattern.
- Typecheck selama membangun; batch pengujian penuh hanya saat diminta; tidak ada fase ditandai lulus sebelum diuji.
- Naikkan versi dan tulis `CHANGELOG.md` saat dikerjakan. Penggabungan ke `main` hanya setelah pemilik meminta.
