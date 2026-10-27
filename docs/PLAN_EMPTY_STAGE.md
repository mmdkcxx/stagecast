# Rencana: tab boleh nol, output menampilkan test pattern

Status: rencana, belum dikerjakan. Berlaku global (akan digabung ke `main`). Belum ada kode yang diubah dan belum diuji.

## Permintaan pemilik

Semua tab bisa ditutup sampai tidak ada yang terbuka. Saat tidak ada tab sama sekali, output menampilkan test pattern.

## Keadaan sekarang (dari kode)

- Tab terakhir tidak bisa ditutup: `TabList.CanClose` dan `CanCloseTab` mensyaratkan lebih dari satu tab, dan self test A3 memeriksa "the last tab cannot be closed" (`SelfTest.cs`).
- Tab yang aktif atau yang sedang program tidak bisa ditutup (`RenderEngine.CloseTab` melempar kesalahan); operator harus memilih atau mengambil tab lain dulu.
- `RenderEngine.Active` melempar kesalahan kalau belum ada tab. Banyak bagian aplikasi mengandalkan adanya tab aktif.

## Rancangan

### Tab boleh nol
- Hapus syarat "lebih dari satu tab" pada `TabList`, dan jadikan tab aktif dan program bisa kosong (nullable) di mesin render.
- Menutup tab yang sedang program (output menyala) meminta konfirmasi di dalam aplikasi, lalu output beralih ke test pattern. Kalau tidak ada output yang menyala, tab langsung ditutup.
- Kontrol yang butuh tab (bilah alamat, maju/mundur, zoom, interaksi, Take, sesi login) dinonaktifkan saat tab nol.
- Daftar tab menampilkan keadaan kosong dengan tombol "Tab baru".
- Konfigurasi yang disimpan boleh berisi nol tab, dan aplikasi membukanya apa adanya (nol tab, test pattern). Ini asumsi saya.

### Test pattern sebagai sumber bingkai
- Sumber tersendiri di samping tab web, bukan tab. Dihasilkan Stagecast pada resolusi dan FPS render yang aktif, lalu masuk ke `FrameHub` seperti sumber lain. Jadi Freeze, Blackout, layar penuh, NDI, dan OMT bekerja tanpa perubahan.
- Isi yang diusulkan, untuk kalibrasi videotron dan pengecekan penerima:
  - bar warna, gradasi abu-abu, dan petak kotak-kotak;
  - bingkai tipis 1 piksel di keempat tepi (cek pemotongan), garis silang tengah, dan diagonal;
  - lingkaran di tengah (cek squeeze anamorfik: harus tampak bulat setelah Resolume/vMix mengembalikan skala);
  - teks ukuran render, FPS, dan nama sumber;
  - elemen bergerak (bar atau penghitung waktu) supaya Freeze dan FPS terlihat.
- Audio senyap secara bawaan. Nada uji adalah opsi terpisah (belum diputuskan).
- Opaque penuh. Opsi alpha NDI/OMT tidak berpengaruh pada pattern.
- Tally di pratinjau memakai aturan yang sama dengan sumber lain.

## Yang harus dikerjakan di kode (perkiraan)
- `RenderEngine`: tab aktif dan program nullable, `CloseTab` boleh menutup tab terakhir dan tab program, sumber test pattern ke `FrameHub`.
- `TabList` dan `MainWindow.Tabs`: aturan tutup, keadaan kosong, kontrol nonaktif.
- Penyimpanan konfigurasi: nol tab.
- Self test A3 diubah: "tab terakhir tidak bisa ditutup" menjadi "menutup tab terakhir menampilkan test pattern".
- Pengujian dan perubahan versi mengikuti kebiasaan proyek (typecheck selama membangun, batch penuh saat diminta, tidak ada fase ditandai lulus sebelum diuji).

## Pertanyaan untuk pemilik
1. Saat pertama kali dibuka (belum ada konfigurasi), mulai dengan nol tab dan test pattern, atau tetap satu tab kosong?
2. Konfigurasi yang disimpan dengan nol tab: dibuka dengan nol tab, atau otomatis dibuatkan satu tab?
3. Menutup tab program saat output menyala: cukup konfirmasi di dalam aplikasi, atau lebih ketat?
4. Perlu tombol untuk menampilkan test pattern kapan saja walau ada tab (misalnya untuk kalibrasi sebelum acara), atau hanya saat tab nol?
5. Perlu nada uji audio 1 kHz sebagai opsi?
