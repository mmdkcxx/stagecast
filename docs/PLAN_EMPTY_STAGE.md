# Rencana: tab boleh nol, output menampilkan test pattern

Status: dikerjakan di `custom` (2026-10-28): tab boleh nol, pola uji per unit (Layar/NDI/OMT) dengan burst audio, tanpa tab tayang saat dibuka. Pola memakai font piksel bawaan (bukan Barlow/IBM Plex) dan tanpa gradasi alpha. Dulu: rencana, belum dikerjakan. Berlaku global (akan digabung ke `main`).

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
- **Isi pola (disetujui pemilik, 2026-10-27):** acuan gambar ada di artifact https://claude.ai/artifact/7XuJE5kDxVjetER6bQdZ7p (privat; contoh di browser, bukan keluaran aplikasi). Susunannya, dari atas ke bawah pada kanvas render:
  - 7 bar warna (putih, kuning, sian, hijau, magenta, merah, biru) setinggi 42% kanvas;
  - gradasi abu-abu 11 langkah (12% tinggi);
  - petak hitam-abu dengan sel 15 piksel pada kanvas 540 piksel (30% tinggi), diskalakan sesuai tinggi render;
  - jalur bawah (sisa tinggi) berisi timecode `HH:MM:SS:FF` di kiri, bar putih yang menyapu kiri ke kanan tiap 2 detik, dan kotak "BEEP nn" di kanan yang menyala putih selama 100 ms tiap detik;
  - garis kisi samar 10 kolom × 6 baris, dua diagonal sudut ke sudut;
  - lingkaran putih di tengah (jari-jari 34% tinggi, dengan tepi hitam tipis) dan garis silang;
  - pelat teks di tengah: ukuran render besar, "STAGECAST TEST PATTERN · nn FPS", dan "RASIO n.nn:1";
  - tanda sudut berbentuk L di empat sudut dan bingkai 1 piksel di keempat tepi.
- Tujuan tiap elemen: bar dan gradasi untuk warna dan kecerahan; bingkai tepi untuk pemotongan; lingkaran untuk cek squeeze anamorfik (harus tampak bulat setelah skala dikembalikan); petak dan diagonal untuk ketajaman dan skala piksel; bar bergerak dan timecode untuk Freeze dan FPS; kotak BEEP untuk sinkron suara dan gambar.
- Semua ukuran dihitung dari lebar dan tinggi render, jadi pola yang sama berlaku untuk 2520×1080, 1920×1080, dan 3840×2160. Teks memakai Barlow dan IBM Plex Mono yang sudah dibundel.
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
- Burst nada 1 kHz, 100 ms, sekali tiap detik, dengan level rendah (usulan: -20 dBFS, sama dengan `ToneGenerator` yang sudah ada), bukan nada terus-menerus.
- Pola menyalakan kilatan atau penghitung pada bingkai yang sama dengan burst, jadi sekaligus bisa dipakai memeriksa sinkron suara dan gambar.
- Bunyi mengikuti unit: NDI yang menampilkan pola mengirim burst di audio NDI, OMT yang menampilkan pola mengirim burst di audio OMT, sedangkan unit yang menayangkan live tetap mengirim audio live. Ini butuh `AudioHub` memilih sumber per unit juga.
- Pengaturan audio (perangkat lokal, meter) tidak berubah.

### Belum diputuskan
- Perangkat audio lokal mengikuti apa saat unit berbeda sumbernya (misalnya Layar menampilkan pola dan NDI live)? Usul: perangkat lokal mengikuti unit Layar.
- Level nada uji dan apakah burst bisa dimatikan terpisah dari pola.
- Blackout dan Freeze terhadap pola (asumsi di atas).

## Rencana eksekusi (nanti)

Aturan proyek berlaku: typecheck selama membangun, batch pengujian penuh hanya saat diminta, dan tidak ada fase ditandai lulus sebelum pengujian dijalankan. Komponen yang sudah ada dan dipakai ulang: `FrameHub` (gerbang Live/Freeze/Blackout, `IFrameSink`), `AudioHub` (`IAudioSink`, nada uji 1 kHz -20 dBFS statis dengan batas 30 detik lewat `ToneGenerator`), `LevelMeter`, dan `FrameCapturer`/`FramePump`.

### E1 - Tab boleh nol dan tidak ada tab live
- `RenderEngine.cs`: `Active` dan `Program` menjadi nullable, hapus pelemparan kesalahan saat belum ada tab, `CloseTab` boleh menutup tab terakhir dan tab program (konfirmasi dilakukan UI), semua pemakai `Active` menangani null.
- `Stagecast.Core/Tabs/TabList.cs`: `CanClose` dan `CanCloseTab` tidak lagi mensyaratkan lebih dari satu tab dan bukan tab program.
- `MainWindow.Tabs.cs`: keadaan kosong di daftar tab ("Tab baru"), kontrol yang butuh tab dinonaktifkan, dialog konfirmasi di dalam aplikasi saat menutup tab program ketika ada output menyala.
- Saat aplikasi dibuka: daftar tab dipulihkan, tidak ada tab yang dipilih live. Pertama kali dibuka: nol tab.
- Lulus kalau: semua tab bisa ditutup, aplikasi tidak error pada nol tab, dan pembukaan ulang tidak membuat tab live.

### E2 - Sumber test pattern
- File baru `Stagecast.Core/Frames/TestPattern.cs`: menggambar bingkai BGRA pada ukuran render, murni dan bisa diuji unit. Isi dan tata letak mengikuti bagian "Isi pola (disetujui pemilik)" di atas; artifact acuan: https://claude.ai/artifact/7XuJE5kDxVjetER6bQdZ7p.
- Dijalankan pada FPS render dan ukuran render yang aktif; diulang saat resolusi diubah.
- Lulus kalau: unit test membuktikan ukuran dan piksel penanda (tepi, pusat, bar) benar pada beberapa resolusi (2520×1080, 1920×1080, 3840×2160).

### E3 - Sumber per unit di FrameHub
- `FrameHub.cs`: setiap sink punya sumber sendiri (live atau pola). Saat tidak ada tab live, sumber efektif semua sink adalah pola. Blackout menang atas pola; Freeze hanya membekukan aliran live (asumsi, perlu konfirmasi).
- `MainWindow.Output.cs`, `MainWindow.Ndi.cs`, `MainWindow.Omt.cs`: saklar "Test pattern" per unit (Layar, NDI, OMT), tidak disimpan sebagai status nyala output (output tetap manual).
- Lulus kalau: unit test `FrameHub` membuktikan sink A menerima pola sementara sink B menerima aliran live, dan gerbang Blackout/Freeze berperilaku seperti yang diputuskan.

### E4 - Audio per unit dan burst bertempo
- `ToneGenerator.cs`: mode burst (1 kHz, 100 ms, sekali per detik, -20 dBFS, tepi naik/turun pendek agar tidak berbunyi klik). Nada uji statis yang sekarang tetap ada dan tidak berubah.
- `AudioHub.cs`: pemilihan sumber audio per sink (live atau burst). Pengaturan audio (perangkat lokal, meter) tidak berubah; perangkat lokal mengikuti unit Layar (asumsi).
- Kilatan di pola dan burst memakai jam yang sama, sehingga sinkron suara dan gambar bisa diperiksa.
- Lulus kalau: unit test membuktikan burst tepat 100 ms per detik pada 48 kHz dan sink yang memilih live tidak menerima burst.

### E5 - Antarmuka
- Saklar "Test pattern" di jendela Layar, NDI, dan OMT, serta penanda "ON · TEST" pada tombolnya, mengikuti `docs/PLAN_TOPBAR_UI.md`. Sebelum top bar selesai, saklar boleh ditaruh sementara di bagian NDI, OMT, dan Layar penuh di sidebar yang ada.
- Keadaan kosong di daftar tab dan teks bantuan di pratinjau saat pola tampil.

### E6 - Pengujian dan rilis
- `SelfTest.cs` A3: "tab terakhir tidak bisa ditutup" diubah menjadi "menutup tab terakhir menampilkan test pattern"; tambah uji untuk pembukaan ulang tanpa tab live.
- Pengujian manual oleh pemilik: videotron (bingkai tepi, lingkaran bulat setelah squeeze), vMix dan Resolume (bunyi dan gambar), sinkron suara-gambar.
- Naikkan versi dan tulis `CHANGELOG.md`; gabung ke `main` setelah lulus.

### Urutan
E1 → E2 → E3 → E4 → E5 → E6. E2 dan E3 bisa dikerjakan tanpa antarmuka baru; E5 menunggu top bar atau memakai sidebar sementara.
