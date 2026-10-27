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

## Keputusan pemilik (2026-10-27)
1. **Pertama kali dibuka:** nol tab dan test pattern.
2. **Konfigurasi tersimpan:** daftar tab dipulihkan, tetapi tidak ada tab yang dipilih live saat aplikasi dibuka, jadi output (kalau dinyalakan) tetap menampilkan test pattern sampai operator memilih tab. Ini mengoreksi asumsi saya sebelumnya ("nol tab dipulihkan apa adanya").
3. **Menutup tab program saat output menyala:** cukup konfirmasi di dalam aplikasi.
4. **Test pattern kapan saja:** boleh, dengan tombol di jendela masing-masing unit: Display (Layar), NDI, dan OMT. Tiap unit berdiri sendiri.
5. **Nada uji:** ada, dibunyikan saat test pattern tampil, berupa burst bertempo (bukan nada statis). Pengaturan audio tetap seperti sebelumnya.

## Dampak rancangan dari keputusan itu

### Keadaan "tidak ada tab live"
Dibedakan dari "nol tab": tab bisa ada di daftar, tetapi belum ada yang live. Di mode Langsung, memilih tab membuatnya live. Pada awal aplikasi tidak ada tab yang dipilih, sehingga output menayangkan pola. Di Preview + Take, program kosong sampai ada Take pertama.

### Test pattern per unit
- Sekarang satu aliran bingkai dibagi ke semua output oleh `FrameHub`. Dengan saklar per unit, tiap output (Layar, NDI, OMT) perlu memilih sumbernya sendiri: aliran live atau pola.
- Pola dihasilkan sekali per bingkai dan dipakai oleh semua unit yang memintanya.
- Saat tidak ada tab live, semua output yang menyala otomatis menampilkan pola (tanpa menyentuh saklar unit).
- Asumsi saya, perlu dikonfirmasi: Blackout tetap menang atas pola (hitam), supaya Blackout selalu aman. Freeze hanya membekukan aliran live; pola tetap bergerak.
- Tombolnya: saklar "Test pattern" di jendela Layar, NDI, dan OMT. Penanda di tombol top bar menjadi "ON · TEST" saat pola aktif. Lihat mockup top bar.

### Nada uji bertempo
- Burst nada 1 kHz, 100 ms, sekali tiap detik, dengan level rendah (usulan: -18 dBFS), bukan nada terus-menerus.
- Pola menyalakan kilatan atau penghitung pada bingkai yang sama dengan burst, jadi sekaligus bisa dipakai memeriksa sinkron suara dan gambar.
- Bunyi mengikuti unit: NDI yang menampilkan pola mengirim burst di audio NDI, OMT yang menampilkan pola mengirim burst di audio OMT, sedangkan unit yang menayangkan live tetap mengirim audio live. Ini butuh `AudioHub` memilih sumber per unit juga.
- Pengaturan audio (perangkat lokal, meter) tidak berubah.

### Belum diputuskan
- Perangkat audio lokal mengikuti apa saat unit berbeda sumbernya (misalnya Layar menampilkan pola dan NDI live)? Usul: perangkat lokal mengikuti unit Layar.
- Level nada uji dan apakah burst bisa dimatikan terpisah dari pola.
- Blackout dan Freeze terhadap pola (asumsi di atas).
