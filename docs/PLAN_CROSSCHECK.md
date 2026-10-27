# Crosscheck Rencana per Tahap

Tanggal pengecekan: 2026-10-27. Penanda: **[V]** = terverifikasi dari sumber saat riset, **[U]** = belum terverifikasi / dari pengetahuan umum, harus dicek saat implementasi.

## Ringkasan temuan

| Hal | Status |
|---|---|
| CefSharp + CEF terbaru (150.x, rilis Jul 2026) | Tersedia [V] |
| Codec H.264/AAC di CEF resmi | **Tidak ada** [V]; paket komunitas `CefSharp.H264` basi (2020-2023) [V] |
| OMT (libomtnet / libvmx / libomt) | MIT, .NET Standard 2.0, BGRA + audio float32 [V] |
| NDI SDK | Gratis, tanpa biaya lisensi; wajib atribusi + EULA [V] |
| FPS 30/60 di CEF OSR | Batas OnPaint 1-60 fps [V] |
| Audio per tab di CEF | Ada audio handler (PCM float32) [V]; OffScreen default mute [V] |

## Keputusan (2026-10-27)

Jawaban pengguna: konten bisa memuat video MP4 (contoh: Canva web), aplikasi hanya untuk tools internal, tidak dijual.

- Opsi D (tanpa H.264) **gugur**. Opsi B (Electron) ditolak karena NDI/OMT/audio butuh native module.
- **Jalur utama: WebView2 (Opsi C).** Edge sudah membawa H.264/AAC dan kompatibel dengan Canva; tidak perlu build Chromium sendiri.
- **Jalur cadangan: CEF dengan codec (Opsi A)**, hanya jika spike WebView2 gagal. Fork CEF milik OBS (`obsproject/cef`) bisa jadi referensi build flag; apakah binari OBS menyertakan H.264 belum terverifikasi [U].
- Karena internal dan tidak dijual, beban lisensi paten H.264 dan atribusi lebih ringan; tetap sertakan notice NDI/OMT bila aplikasi dibagikan ke pihak lain.
- Catatan di Tahap 1, 2, 5 di bawah yang menyebut CEF/CefSharp kini berlaku untuk **jalur cadangan**; untuk jalur utama lihat bagian "Tahap 1 (revisi)".

## Tahap 1 (revisi) - Spike WebView2 + capture

**Pendekatan:** `CreateCoreWebView2CompositionController` dengan `RootVisualTarget` berupa ContainerVisual; ukuran diatur lewat `Bounds` [V: API ada]. Frame diambil dengan Windows.Graphics.Capture dari visual tersebut; pola ini dipakai plugin Flutter dan GStreamer, resize = buat ulang frame pool [V].
Input mouse diteruskan lewat `SendMouseInput`, pen/touch lewat `SendPointerInput` [V].

**Yang harus dibuktikan di spike (belum terverifikasi):**
1. `GraphicsCaptureItem.CreateFromVisual` bekerja pada visual WebView2 yang ukurannya (2520x1080) melebihi atau tidak tampil di monitor mana pun [U].
2. Capture stabil di 30 dan 60 fps; halaman diam tidak menghasilkan frame sehingga FrameHub mengulang frame terakhir. WPF `WebView2CompositionControl` sendiri bisa lebih rendah fps-nya karena memakai capture [V], jadi gunakan hosting sendiri, bukan kontrol itu.
3. Penanganan DPI: `RasterizationScale` agar 1 piksel CSS = 1 piksel output pada skala Windows 125/150% [U].
4. Video MP4 dan Canva presentation terputar dan terlihat benar di hasil capture.
5. Audio: WASAPI process loopback menerima ID proses dan mode include-process-tree; contoh resmi Microsoft ada, butuh Windows build 20348+ (Windows 11 memenuhi) [V]. Proses WebView2 adalah anak proses aplikasi, jadi menangkap pohon proses aplikasi sendiri seharusnya cukup [U]. Bila proses target tidak mengeluarkan audio, yang diterima hanya senyap [V].
6. Mute tab non-aktif lewat properti `IsMuted` pada CoreWebView2 [U: belum dikonfirmasi di dokumentasi].
7. Satu `CoreWebView2Environment` untuk semua tab (hemat memori) vs satu per tab (isolasi audio); pilih setelah uji.

**Wrapper audio C#:** Tidak ditemukan dukungan process loopback di NAudio dari riset [U]; kemungkinan perlu P/Invoke sendiri ke `ActivateAudioInterfaceAsync` atau memakai contoh Microsoft sebagai dasar.

**Lisensi WebView2:** SDK dan Evergreen Runtime gratis untuk didistribusikan [U: konfirmasi di halaman lisensi Microsoft]. Windows 11 umumnya sudah menyertakan runtime.

**Kriteria lulus:** semua butir 1-5 berhasil di mesin Windows 11 target. Gagal di 1 atau 2 = pindah ke jalur cadangan CEF.

---

## Tahap 1 - Prototipe inti + uji codec

**Kelayakan:** Tinggi. Pakai `CefSharp.OffScreen.NETCore` (bukan kontrol WPF) supaya browser tidak terikat UI; preview digambar sendiri dari FrameHub.
Resolusi = ukuran viewport browser OSR; mengubahnya lewat resize tanpa reload. [U: perilaku persis diuji di prototipe]

**Lisensi:** CEF dan CefSharp berlisensi BSD [U: halaman NuGet tidak menampilkan SPDX; baca file LICENSE di repo sebelum rilis]. Wajib menyertakan notice Chromium/CEF di distribusi.

**Masalah utama - codec:**
- Build CEF resmi hanya berisi codec terbuka (VP8/VP9, Opus, Vorbis). H.264/AAC tidak ada, jadi MP4 H.264 gagal diputar [V].
- Paket `CefSharp.H264` hanya sampai CefSharp 79 (2020) dan x86 109 (2023) [V]; tidak cocok dengan CEF 150.

**Opsi jika konten Anda butuh H.264:**
- A. Build CEF sendiri dengan `proprietary_codecs=true` + `ffmpeg_branding=Chrome`. Berat (build Chromium, disk besar, waktu lama), dan tanggung jawab lisensi paten ada di Anda. [U: skala usaha]
- B. Electron: ffmpeg bawaannya menyertakan codec proprietary [V]. OSR punya mode bitmap CPU dan shared texture GPU [V], tapi NDI/OMT/audio perlu native module (OMT .NET tidak langsung dipakai).
- C. WebView2 (Edge, sudah ada H.264) + Windows.Graphics.Capture dari visual hosting [V: pendekatan dipakai plugin Flutter/GStreamer]. Audio lewat WASAPI process loopback. Lebih rumit; ukuran viewport > layar dan fps 60 stabil belum terbukti [U].
- D. Tetap CEF resmi, dan konten dibuat dengan VP9/WebM atau tanpa video (animasi/HTML/canvas jalan normal).

**Paten H.264:** Pool H.264 dulu menyatakan tidak menarik royalti untuk internet video gratis bagi pengguna akhir [V, sumber lama 2010], tapi penggunaan komersial lain berbayar dan syarat terkini belum dicek [U]. Perlu ditinjau bila memilih A atau B untuk distribusi komersial.

**Uji gate:** Muat 3 halaman nyata Anda (animasi, video MP4, video WebM/YouTube) pada 2520x1080, lalu lihat apakah tampil + berbunyi. Hasilnya menentukan opsi A-D.

---

## Tahap 2 - Multi-tab

**Kelayakan:** Tinggi. Satu browser OSR per tab; FrameHub hanya menerima frame dari tab aktif.
**Tab tidak aktif:** `WasHidden(true)` mengurangi render, tapi halaman tetap hidup [U: efek ke timer/video di halaman perlu diuji; beberapa halaman men-throttle sendiri saat hidden]. Opsi "Keep live" mungkin butuh tetap visible dengan frame dibuang.
**Memori:** Tiap tab = satu proses renderer Chromium; batasi jumlah tab (uji 5-10 tab di resolusi 2520x1080: tiap buffer BGRA sekitar 10,9 MB).
**Lisensi:** Tidak ada tambahan.

---

## Tahap 3 - Display output (monitor extended, fullscreen)

**Kelayakan:** Tinggi. Enumerasi monitor lewat Win32/`Screen`, jendela borderless di bounds monitor. Wajib per-monitor DPI aware (manifest).
**Rendering:** Mulai dengan `WriteableBitmap` (WPF). Pada 2520x1080@60 salinan 10,9 MB/frame (sekitar 650 MB/s) mungkin tersendat [U]. Cadangan: swapchain Direct3D 11 lewat Vortice.Windows (MIT [U]) atau Silk.NET (MIT [U]).
**Skala (diperbarui):** HDMI/monitor hanya 1920x1080 sementara render 2520x1080, jadi mode utama adalah Peras (anamorfik): lebar 76,2%, tinggi 100%; di Resolume/vMix lebar diskalakan balik ke 131,25%. Fit dan 1:1 tetap tersedia untuk kasus lain.
**Kualitas:** Memperkecil lalu memperbesar lagi melepas sebagian detail horizontal (1920 piksel membawa 2520 piksel asli). Pilihan filter menentukan ketajaman, dan akan diukur di spike S11 [U]. Jalur NDI/OMT mengirim 2520 asli sehingga tidak kehilangan detail ini.
**Kemungkinan lain [U]:** Beberapa GPU dan LED processor bisa menerima resolusi custom lewat HDMI (misalnya 2520x1080) sehingga tidak perlu diperas. Ini bergantung pada input processor LED, jadi hanya kemungkinan yang bisa ditanyakan, bukan rencana.
**Lisensi:** Pustaka grafis di atas MIT [U: konfirmasi].

---

## Tahap 4 - Toggle FPS 30/60

**Kelayakan:** Tinggi. `windowless_frame_rate` maksimum 60, default 30, bisa diubah saat runtime lewat `SetWindowlessFrameRate` [V]. 30/60 berada dalam batas.
**Catatan:** Fps aktual bisa lebih rendah jika halaman berat [V]. Halaman diam tidak menghasilkan frame, jadi FrameHub harus mengulang frame terakhir pada laju target (sudah ada di rencana).
**Lanjutan:** Mode `external_begin_frame_enabled` bisa dipakai bila butuh pacing yang presisi [V ada, U efeknya].
**Lisensi:** Tidak ada.

---

## Tahap 5 - Audio pipeline

**Kelayakan:** Sedang-tinggi.
- CEF audio handler memberi PCM float32 beserta jumlah channel dan sample rate saat stream dimulai [V].
- CefSharp.OffScreen menambahkan switch `mute-audio` secara default; harus memanggil `EnableAudio()` / menghapus switch [V].
- Routing audio per instance browser terbatas menurut satu forum informal [V, kualitas sumber rendah]; uji dengan 2+ tab yang bersuara.
**Output lokal ke perangkat pilihan:** WASAPI via NAudio (MIT [U]) dengan pemilihan perangkat. Perlu resampling + buffer untuk drift clock antara capture dan perangkat.
**Suara dobel:** Pastikan suara hanya keluar lewat jalur kita ketika handler aktif [U: diuji].
**Sinkronisasi A/V:** Satu clock monotonic untuk timestamp video dan audio.
**Lisensi:** NAudio MIT [U].

---

## Tahap 6 - NDI output

**Kelayakan:** Tinggi. BGRA didukung (stride = lebar x 4) dan audio float 32-bit direkomendasikan [V].
**Wrapper:** NuGet `NDILibDotNetAdvanced` hanya wrapper terkelola; tidak menyertakan atau melisensikan runtime native, dan fitur Advanced butuh lisensi sendiri [V]. Saran: P/Invoke tipis sendiri ke `Processing.NDI.Lib.x64.dll` (API kecil) untuk menghindari ketergantungan wrapper pihak ketiga.
**Lisensi (Basic SDK):**
- Boleh dipakai di produk gratis atau komersial tanpa biaya lisensi [V].
- Boleh mendistribusikan DLL jika EULA aplikasi Anda mencakup syarat EULA NDI SDK [V].
- Taruh DLL di folder aplikasi, jangan di system path [V].
- Wajib tautan ke ndi.video di dekat penyebutan NDI di produk, situs, dan dokumentasi [V].
- Sertakan file notice lisensi NDI; jaga versi redistributable tetap terbaru [V].
- Fitur Advanced (misal pengiriman GPU) butuh vendor ID dari NDI [V]; tidak dibutuhkan untuk rencana ini.
**Tidak boleh:** Mendistribusikan NDI Tools; cukup menautkan ke ndi.video/tools [V].

---

## Tahap 7 - OMT output

**Kelayakan:** Tinggi.
- Organisasi OMT di GitHub: `libomtnet` (C#, .NET Standard 2.0, bisa dipakai .NET 8), `libvmx` (C, codec), `libomt` (wrapper C), semuanya **MIT** [V].
- Pengirim: `OMTSend` + `OMTMediaFrame`; format video kirim termasuk **BGRA** (mendukung alpha); audio float32 hingga 32 channel [V].
- Aplikasi wajib menyertakan `libvmx.dll` di folder yang sama [V].
- vMix 29 (rilis 27 Okt 2025) mendukung OMT [V]; pastikan vMix Anda versi 29+.
**Yang belum jelas:** Mekanisme discovery di Windows (di Mac pakai Bonjour, Linux Avahi) [V sebagian]; cara Windows menanganinya perlu dibaca di dokumentasi/kode [U]. Pastikan jaringan LAN gigabit [V dari rilis vMix].
**Lisensi:** MIT, cukup sertakan notice. Status sumber terbuka sempat belum final pada Agustus 2025 [V]; repo kini terlihat publik dengan lisensi MIT [V].

---

## Tahap 8 - Stabilitas

**Kelayakan:** Tinggi, tapi butuh waktu uji nyata.
**Cek:** Monitor dicabut/dicolok saat live, tab crash (renderer Chromium bisa crash tanpa menjatuhkan aplikasi), kebocoran memori setelah berjam-jam, auto-start dan restore profil, jaringan NDI/OMT putus lalu pulih.
**Uji beban:** 2520x1080@60 dengan display + NDI + OMT + audio sekaligus; catat CPU, RAM, dan frame yang dibuang.

---

## Tahap 9 - Packaging

**Kelayakan:** Tinggi.
**Isi paket:** Aplikasi .NET 8, folder CEF (besar, ratusan MB [U]), `Processing.NDI.Lib.x64.dll`, `libvmx.dll`.
**Installer:** Inno Setup atau MSIX; juga opsi folder portable. NDI Runtime tidak perlu dipasang terpisah bila DLL dibundel sesuai aturan di atas [V].
**Notice wajib:** CEF/Chromium (BSD + pihak ketiga), NDI (notice + tautan ndi.video), OMT/libvmx (MIT).
**Code signing:** Opsional tapi mengurangi peringatan SmartScreen [U].

---

## Urutan kerja yang disarankan

1. Gate codec (Tahap 1) dengan halaman nyata Anda. Putuskan A/B/C/D.
2. Lanjut Tahap 2-4, lalu Tahap 5-7.
3. Tahap 8-9 setelah semua output jalan.

## Pertanyaan yang menentukan

- Apakah konten event Anda memakai video MP4/H.264, atau terutama animasi/HTML/WebM/YouTube?
- Apakah aplikasi ini hanya dipakai internal, atau akan didistribusikan/dijual? (menentukan beban lisensi H.264 dan atribusi)


## Tambahan setelah crosscheck awal (2026-10-27)

Fitur yang ditambahkan sesudah dokumen ini ditulis. Belum ada yang diuji; semuanya [U] sampai spike terkait dijalankan.

- **Login (Canva, Kahoot, Slido):** WebView2 memakai folder data permanen sehingga sesi bertahan. Risiko: popup login Google/Microsoft (`NewWindowRequested`) dan kemungkinan Google menolak browser tertanam; kebijakan autoplay; dua profil terpisah bersama loopback audio. Diuji di S6, dibangun di A3c.
- **Preview + Take:** butuh dua tab dirender dan di-capture bersamaan; beban CPU/GPU dan kebocoran audio tab preview ke loopback proses belum diketahui. Diuji di S12, dibangun di A3b.
- **Test tone:** generator sinyal sederhana di AudioHub; tidak ada risiko lisensi. Yang perlu dicek hanya urutan kanal kiri/kanan di NDI/OMT (A5-A7).
- **Preset resolusi dan batas ukuran:** batas 100-8192 piksel di mockup hanya usulan; batas nyata bergantung WebView2 dan GPU, diuji di S3.
