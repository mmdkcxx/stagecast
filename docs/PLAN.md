# Rencana: Stagecast (Virtual Viewport Renderer, Windows 11)

Aplikasi desktop untuk merender halaman web pada resolusi custom (contoh LED videotron 2520x1080) tanpa bergantung ukuran jendela, lalu mengeluarkannya ke monitor extended (fullscreen), NDI, dan OMT (vMix), lengkap dengan audio.

## Kebutuhan

- Resolusi render bisa diketik sendiri (W x H), terlepas dari ukuran jendela/monitor.
- FPS toggle 30 / 60.
- Multi-tab; tab aktif menjadi sumber output, pindah tab = output ikut pindah.
- Output: fullscreen ke monitor pilihan (HDMI 1/2/...), NDI, OMT.
- Monitor/HDMI berresolusi 1920x1080 sementara render 2520x1080: output layar harus diperas (anamorfik) lalu diskalakan balik di Resolume/vMix.
- Audio halaman dikirim lewat NDI/OMT; perangkat audio output lokal bisa dipilih.
- Browser berbasis Chromium open source.
- Platform: Windows 11 x64. Satu viewport dulu.

## Framework

C# .NET 8 + WPF + **WebView2 (Edge/Chromium) dengan visual hosting** + Windows.Graphics.Capture untuk mengambil frame.

Keputusan 2026-10-27: konten bisa berupa video MP4/H.264 (misalnya dari Canva web) dan aplikasi hanya untuk internal.
CEF resmi tidak punya H.264, sedangkan WebView2 memakai Edge yang sudah punya H.264/AAC dan kompatibel dengan situs seperti Canva.

- Viewport W x H diatur lewat ukuran composition controller (bukan ukuran jendela); frame diambil dari visual lewat Windows.Graphics.Capture.
- Audio: WASAPI process loopback pada proses aplikasi sendiri beserta turunannya (WebView2 berjalan sebagai proses anak); tab non-aktif di-mute.
- NDI/OMT tetap dari C#.

**Cadangan** jika spike (Tahap 1) gagal: CefSharp/CEF build sendiri dengan codec proprietary (referensi: fork CEF milik OBS).
Tauri ditolak (tidak ada akses frame), Electron ditolak (NDI/OMT/audio butuh native module).

## Arsitektur

```
 Tab A ─┐
 Tab B ─┼─(tab aktif)─► FrameHub + AudioHub ─► Preview / Display / NDI / OMT
 Tab C ─┘
```

Satu sumber frame, banyak sink. Tiap sink punya thread dan antrean sendiri; sink lambat membuang frame, tidak menghambat yang lain.

## Modul

1. RenderEngine - satu WebView2 (composition controller) per tab, resolusi/FPS/DPI global, preset resolusi (bawaan + preset buatan sendiri yang bisa disimpan dan dihapus); frame diambil via Windows.Graphics.Capture.
2. FrameHub - ring buffer 2-3 slot, fan-out ke sink, statistik, ulangi frame terakhir bila halaman statis. Freeze/Blackout hanya mengubah apa yang dikirim ke sink; capture dan input halaman tetap berjalan sehingga halaman bisa disiapkan di balik layar.
3. DisplayOutput - daftar monitor, jendela borderless fullscreen, mode skala **Peras/anamorfik (utama)**/Fit/1:1, opsi selalu di atas (topmost), tangani monitor dicabut.
   Konteks: HDMI hanya 1920x1080, sedangkan render 2520x1080. Gambar diperas horizontal (lebar 76,2%) ke 1920x1080, lalu di Resolume/vMix lebarnya diskalakan balik ke 131,25%. UI menampilkan faktor skala ini. Peras hanya berlaku untuk output HDMI/layar. NDI dan OMT selalu mengirim resolusi render native yang dipilih (dikonfirmasi pengguna).
4. NdiOutput - sender NDI video+audio, BGRA+alpha, nama sumber bisa diubah dari UI lewat ikon pena (disimpan di profil; mengubah nama saat nyala membuat ulang sender).
5. OmtOutput - sender OMT video+audio untuk vMix.
6. AudioHub - capture audio lewat WASAPI process loopback (proses aplikasi + anak), tab non-aktif di-mute, mixer/volume/mute, test tone (sinus 1 kHz / pink noise, -20 dBFS, ke Lokal/NDI/OMT), kirim ke NDI/OMT dan ke perangkat audio lokal (WASAPI) pilihan.
7. Input/kontrol halaman - preview bisa diperbesar (Fit/100%, geser), jendela preview terpisah, zoom halaman, navigasi (kembali/maju/muat ulang); mouse, scroll, dan keyboard diteruskan ke halaman dengan pemetaan koordinat ke viewport; tombol Interaksi untuk mencegah klik tak sengaja; output fullscreen tanpa input; opsional kontrol HTTP lokal.
8. Konfigurasi - profil JSON (URL, tab, nama tab, resolusi dan preset, FPS, monitor, skala, selalu di atas, NDI/OMT, profil per tab; tanpa kredensial), auto-start.

## Perilaku multi-tab

- Resolusi dan FPS global untuk semua tab (format stream tetap saat pindah tab).
- Hanya tab aktif mengisi output; tab lain hidup di latar belakang (throttled), opsi "Keep live" per tab.
- Saat pindah tab, frame terakhir ditahan sampai frame pertama tab baru tiba.
- Audio hanya dari tab program (yang tayang); tab preview dibisukan.
- Dua cara pindah tab: Langsung, atau Preview + Take (tab preview dirender dan dioperasikan terpisah dari tab program; TAKE menukar keduanya).
- Sesi login: folder data browser permanen bersama semua tab (login sekali bertahan setelah restart); profil terpisah per tab sebagai opsi; popup login ditangani; password tidak disimpan aplikasi; tombol Hapus data login; cek sesi sebelum acara.
- Nama tab bisa diubah operator (label saja, disimpan di profil).

## Tahapan

1. Spike WebView2 (batas waktu singkat): buktikan (a) viewport 2520x1080 lepas dari ukuran jendela/monitor, (b) capture 30/60 fps stabil, (c) video MP4 dan Canva terputar, (d) audio bisa diambil lewat process loopback. Gagal = pindah ke jalur cadangan CEF.
2. Multi-tab.
3. Display output (monitor, fullscreen, skala).
4. Toggle FPS 30/60.
5. Audio pipeline (capture, AudioHub, perangkat lokal).
6. NDI output (video + audio).
7. OMT output (video + audio), uji dengan vMix.
8. Stabilitas (profil, auto-start, monitor dicabut, uji berjam-jam).
9. Packaging (installer/portable, cek dependensi).

## Risiko awal

- Codec: teratasi dengan WebView2; risiko bergeser ke kelayakan capture (lihat spike Tahap 1).
- Performa pada resolusi besar dengan NDI+OMT+display bersamaan (salinan CPU).
- DPI/scaling Windows (perlu per-monitor DPI aware).
- Sinkronisasi A/V dan suara dobel.
- Lisensi NDI dan OMT untuk distribusi.

(Hasil crosscheck per tahap: lihat PLAN_CROSSCHECK.md)
