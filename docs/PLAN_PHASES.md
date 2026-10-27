# Rencana Fase demi Fase

Prinsip: setiap langkah kecil, bisa dijalankan, dan punya kriteria lulus yang jelas. Jangan lanjut ke langkah berikutnya sebelum yang sebelumnya lulus.

Pengujian dijalankan per kelompok fase, bukan tiap fase; lihat `TESTING.md`.

Penanda verifikasi: **[Saya]** = saya cek lewat build/log/angka; **[Anda]** = hanya bisa dinilai dengan melihat/mendengar di layar Anda.

Lingkungan: Windows 11 (build 26300), .NET SDK 8.0.301, WebView2 Runtime 155, GPU AMD iGPU + NVIDIA GTX 1650.
Lokasi: kode spike di `spikes/`, dibangun dan dijalankan di Windows dari salinan `C:\dev\stagecast` lewat `scripts/run-spike.sh`.

---

## BAGIAN 1 - SPIKE (proyek sekali pakai)

Tujuan: membuktikan jalur WebView2 + capture layak. Hasilnya angka dan keputusan, bukan produk.

### S0 - Kerangka proyek
- Buat solusi WPF .NET 8 dan tambahkan paket WebView2.
- Skrip salin ke `C:\dev` + build + jalankan dari WSL.
- Jendela kosong yang terbuka.
- **Lulus:** build tanpa error dan jendela muncul. [Saya]
- **Status: lulus (2026-10-27).** Build 0 peringatan 0 error; jendela terbuka dan menulis `logs/s0.log` (runtime .NET 8.0.21, Windows 10.0.26300, jendela 720x420). Paket `Microsoft.Web.WebView2` 1.0.4314.35 sudah ditambahkan tetapi belum dipakai. Jalankan dengan `scripts/run-spike.sh`.

### S1 - WebView2 biasa
- Tampilkan satu WebView2 standar yang memuat URL.
- Tujuan: memastikan runtime, jaringan, dan penyalinan folder berfungsi sebelum menambah kerumitan.
- **Lulus:** halaman web tampil. [Anda]
- **Status:** [Saya] lulus (2026-10-27): runtime WebView2 155.0.4283.62, `https://example.com/` selesai dimuat (`success=True`, judul 'Example Domain'), folder data permanen di `%LocalAppData%\Stagecast\Spike\WebView2`. [Anda] menunggu: lihat jendela saat `scripts/run-spike.sh run` (tampil 8 detik).

### S2 - Composition hosting
- Ganti ke `CreateCoreWebView2CompositionController` dengan `RootVisualTarget` berupa ContainerVisual.
- Teruskan mouse dengan `SendMouseInput`.
- Visual belum ditampilkan ke layar; cukup pastikan WebView terbuat dan memuat halaman.
- **Lulus:** event navigasi selesai (`NavigationCompleted`) tercatat di log tanpa jendela web yang terlihat. [Saya]
- **Status: lulus (2026-10-27).** `CreateCoreWebView2CompositionControllerAsync` dengan `RootVisualTarget` berupa `ContainerVisual` (Windows.UI.Composition, tidak dipasang ke jendela mana pun) berhasil; `NavigationCompleted success=True`; halaman melaporkan `window.innerWidth/innerHeight` = 2520x1080 pada skala 100% (S3 masih menguji skala 150% dan resolusi lain). Tidak ada kontrol WebView2 di jendela. DispatcherQueue dibuat lewat `CoreMessaging.dll` (`CreateDispatcherQueueController`). Catatan: belum terbukti bahwa halaman benar-benar dirender ke visual itu; itu diuji di S4 (capture).
- **Risiko:** hosting visual butuh DispatcherQueue dan Compositor di thread yang tepat.

### S3 - Viewport 2520x1080
- Atur `Bounds` ke 2520x1080 dan uji `RasterizationScale`.
- Pastikan `window.innerWidth`/`innerHeight` yang dilaporkan halaman tepat 2520x1080 (jalankan skrip di halaman dan catat hasilnya).
- Uji juga resolusi lain (1920x1080, 3840x1080, 1080x1920) dan ubah ukuran saat berjalan.
- **Lulus:** angka dari halaman cocok dengan yang diketik, di skala Windows 100% dan 150%. [Saya]
- **Status: lulus dengan simulasi (2026-10-27).** Skala disimulasikan lewat `RasterizationScale` (1.0, 1.25, 1.5, 2.0) dengan `ShouldDetectMonitorScaleChanges = false`, bukan lewat pengaturan skala Windows asli. Setiap skala x 4 resolusi (2520x1080, 1920x1080, 3840x1080, 1080x1920) = 16 kasus, semuanya lulus: halaman melaporkan `innerWidth x innerHeight` persis sama dengan resolusi CSS yang dipilih dan `devicePixelRatio` sama dengan skalanya. Semua kasus dijalankan berurutan pada satu browser yang hidup, jadi ukuran diubah saat berjalan tanpa membuat ulang browser.
  - **Aturan yang dipakai:** `Bounds` (piksel fisik) = resolusi CSS x skala (`RenderSurface.PhysicalSize` di `Stagecast.Core`, 8 tes unit).
  - **Keputusan desain:** aplikasi sebaiknya mengunci `RasterizationScale = 1.0`. Jika tidak, resolusi 2520x1080 pada 150% membutuhkan permukaan 3780x1620 (2,25x piksel) untuk hasil CSS yang sama, tanpa manfaat untuk output.
  - **Belum teruji:** skala Windows yang asli (DPI jendela WPF, perilaku capture di monitor 150%) tetap perlu dicoba di S4/S5 bila mesin target memakai skala selain 100%.

### S4 - Capture satu frame
- **Status: lulus (2026-10-27).** `GraphicsCaptureItem.CreateFromVisual` bekerja untuk visual yang tidak terpasang ke jendela dan lebih lebar dari layar (2520 px). Frame tiba dalam ~35 ms, ukuran konten dan tekstur 2520x1080 (B8G8R8A8), tersimpan sebagai PNG. Saya melihat PNG: berisi halaman example.com yang dirender utuh, bukan hitam. Jalur cadangan CEF tidak diperlukan. Sisa untuk [Anda]: konfirmasi isi PNG di `logs\s4-frame.png`. Belum diuji: frame berkelanjutan (S5), dua capture bersamaan (S12), video H.264.
- Buat `GraphicsCaptureItem` dari visual, frame pool Direct3D 11, ambil satu frame, simpan sebagai PNG.
- Ukuran PNG harus 2520x1080.
- **Lulus:** PNG berisi halaman yang benar dengan ukuran tepat. [Anda untuk isi, Saya untuk ukuran]
- **Titik keputusan awal:** Jika `CreateFromVisual` tidak mau bekerja untuk visual ini atau yang melebihi layar, berhenti di sini dan evaluasi jalur cadangan.

### S5 - Capture berkelanjutan + preview + statistik
- **Status: lulus pada angka rata-rata, dengan catatan (2026-10-27).** Hasil [Saya]:
  - 5 menit, target 60 fps, tanpa preview: 17.999 frame terkirim = 60,0/dtk, tidak ada satu detik pun di bawah 58 fps, CPU proses 0,3%. Target 30 fps: 30,0/dtk.
  - **Temuan 1:** WGC secara bawaan hanya mengirim ~48 frame/dtk untuk visual ini (layar 144 Hz). `GraphicsCaptureSession.MinUpdateInterval = 1 ms` membuatnya mengirim tiap frame compositor (90-140/dtk), lalu kita mengatur laju sendiri dengan jadwal tetap. Properti ini butuh proyeksi SDK 10.0.26100 (`WindowsSdkPackageVersion`) dan Windows 11; fallback di Windows lama belum diuji.
  - **Temuan 2:** laju compositor tidak tetap (tercatat 88-143/dtk antar run), sehingga jarak antar frame yang dikirim kadang 28-49 ms. Pada run 5 menit, 409 dari 17.999 interval (2,3%) lebih dari 25 ms; maks 48,6 ms. Rata-rata tepat, kerataannya belum sempurna. Output nyata (S6+) harus memakai timer tetap yang mengambil frame terbaru, bukan meneruskan frame mentah.
  - Pembacaan frame untuk jendela preview (CPU readback, 4x/dtk) menambah stutter dan hanya cocok untuk spike; jalur output asli memakai GPU.
  - GPU % belum diukur (hanya CPU).
  - **[Anda]:** lihat jendela preview dan nilai kelancarannya (`scripts/run-spike.sh run` lalu pilih mode s5 bila perlu).
- Ambil frame terus-menerus, tampilkan di jendela preview yang diskalakan.
- Tampilkan: fps masuk, waktu antar frame, frame yang dibuang, pemakaian CPU/GPU.
- Toggle target 30 dan 60 fps.
- Uji dengan halaman animasi (misal tes animasi di browser) agar frame terus datang.
- **Lulus:** 60 fps stabil (misal minimal 55 rata-rata, tanpa stutter nyata) selama 5 menit. [Saya untuk angka, Anda untuk kelancaran visual]

### S6 - Konten nyata
- **Status: lulus (2026-10-27).** [Anda] mencoba sesi interaktif dan melaporkan "aman" (login, sesi, konten); rincian butir yang dicoba tidak dilaporkan satu per satu, jadi keyboard dan dua profil tetap diperlakukan sebagai belum terbukti secara eksplisit di log.
- (Catatan lama:) setengah selesai.
  - **[Saya] lulus:** video MP4 H.264 720p (Big Buck Bunny, uji publik; tidak disimpan di repo) terputar di visual composition: `canPlayType` = "probably", 330 frame, 0 frame dibuang, frame ditangkap berwarna wajar (PNG saya lihat sendiri). Kebijakan autoplay bawaan **memblokir** pemutaran bersuara tanpa klik (`NotAllowedError`); dengan argumen `--autoplay-policy=no-user-gesture-required` diizinkan. Jadi aplikasi asli perlu argumen itu untuk Kahoot/Canva yang berbunyi tanpa klik.
  - **Harness interaktif:** `scripts/run-spike.sh browse [profil] [url]` membuka jendela dengan preview, mouse diteruskan ke halaman lewat `SendMouseInput`, popup dibuka di tab yang sama (kebijakan yang diuji), profil WebView2 permanen per nama profil, dan log jumlah cookie Canva/Kahoot/Slido saat start (nilai cookie tidak dicatat). Cookie Canva sudah ada 8 di profil `main`... **catatan:** itu dari kunjungan uji otomatis saya, bukan login.
  - **Hasil sesi interaktif pertama (log 14:24-14:27, [Anda] memakai harness):** login Canva lewat Google **berhasil di browser tertanam**, tidak ditolak Google. Alurnya: popup `oauth/authorize/GOOGLE` dibuka di tab yang sama, lewat accounts.google.com (identifier, CheckCookie, SetSID, consent), kembali ke `canva.com/oauth/authorized/google`, lalu Canva terbuka. Membuka desain di editor (`NewWindowRequested`, IsUserInitiated=True) juga berjalan lewat kebijakan tab yang sama. Sekali `ConnectionAborted` saat halaman callback dimuat ulang (kemungkinan kode OAuth sekali pakai), tidak berdampak. Ketikan di halaman Google (waktu 70 dtk di halaman identifier) menunjukkan keyboard kemungkinan besar diteruskan, tetapi belum dikonfirmasi eksplisit [U]. Laju frame mengikuti isi halaman: 20-75 frame masuk/dtk saat Canva aktif.
  - **[Anda] belum (sisa):** tutup-buka cek sesi (jalankan `browse main` lagi: log `cookies for https://www.canva.com` harus lebih banyak dari 8 dan Canva langsung login), konfirmasi keyboard, video Canva, dua profil. (Sebelumnya:) login Canva (juga lewat Google), tutup-buka cek sesi, popup login, putar video Canva, dua profil. Keyboard ke halaman belum terbukti [U]; harness baru meneruskan mouse.
- Muat video MP4 (H.264) langsung, lalu presentasi/video dari Canva web.
- Uji login: login Canva (termasuk lewat Google) dilakukan sekali oleh Anda; profil WebView2 permanen menyimpan sesi.
- Tutup dan buka aplikasi, lalu cek sesi Canva masih aktif.
- Uji popup login (`NewWindowRequested`): tombol "Login dengan Google/Microsoft" harus bisa menyelesaikan login. Catat apakah Google menolak dengan pesan "browser tidak aman" [U]; cadangan: login email+password atau QR.
- Uji kebijakan autoplay agar video/suara di Canva atau Kahoot tidak diblokir sebelum ada klik [U].
- Uji dua profil terpisah (dua akun Canva): sesi tidak tercampur, loopback audio tetap menangkap keduanya [U].
- **Lulus:** video terputar halus dan warna benar di preview. [Anda]

### S7 - Pemilihan GPU
- **Keputusan [Anda]:** pemilihan GPU diatur operator lewat Windows Graphics Settings (per aplikasi); aplikasi tidak menyediakan pilihan GPU sendiri. Aplikasi cukup menampilkan nama GPU yang dipakai di panel status/dokumentasi operator.
- **Status: lulus dengan bukti tipis (2026-10-27).** `scripts/gpu-compare.ps1` memaksa spike dan `msedgewebview2.exe` ke tiap GPU lewat preferensi grafis Windows per aplikasi (dikembalikan setelah uji), lalu menghitung pemakaian mesin GPU nyata dari counter Windows. Satu laptop hybrid (AMD iGPU menggerakkan layar, GTX 1650 dGPU), 40 dtk per kasus:

  | GPU dipaksa | Konten | fps terkirim | interval >25 ms | CPU | GPU yang terpakai |
  |---|---|---|---|---|---|
  | iGPU AMD | animasi 60 fps | 60,0 | 4 | 0,3% | AMD 3D ~6% |
  | GTX 1650 | animasi 60 fps | 60,0 | 58 | 0,3% | NVIDIA copy ~12% + AMD 3D ~10% |
  | iGPU AMD | video 30 fps | 30,0 (isi video 30 fps) | n/a | 0,1% | AMD 3D ~3,5%, video ~4% |
  | GTX 1650 | video 30 fps | 30,0 | n/a | 0,0% | NVIDIA 3D ~8%, video ~5% + AMD 3D ~3% |

  - Keduanya mencapai 60 fps. Pada GTX, **kedua GPU tetap bekerja**: hasil render NVIDIA disalin ke AMD karena layar laptop dikendalikan AMD. Ini menambah salinan lintas-GPU dan, pada satu run ini, jarak antar frame lebih tidak rata (58 vs 4 interval >25 ms).
  - **Rekomendasi sementara:** biarkan iGPU (bawaan "hemat daya") untuk capture. Lebih sedikit salinan, lebih rata, beban kecil. GTX baru layak dicoba bila halaman sangat berat (Canva dengan animasi banyak) atau bila monitor HDMI ke event dikendalikan GTX.
  - **Batas bukti:** satu run per kasus, hanya 2-6 sampel counter, tanpa halaman berat, dan interval stutter sangat bervariasi antar run (S5 menunjukkan 2-20 per 1200 frame pada kondisi sama). Uji ulang di mesin event dengan konten asli sebelum memutuskan. Video 30 fps tidak berguna untuk menilai kerataan 60 fps.
- Ukur S5 dan S6 dengan aplikasi dipaksa ke GTX 1650 dan ke iGPU AMD (setting grafis Windows per aplikasi).
- Catat mana yang lebih stabil.
- **Lulus:** ada rekomendasi GPU berdasarkan angka. [Saya]

### S8 - Audio loopback
- **Status: lulus pada angka, [Anda] belum mendengar (2026-10-27).** `ProcessLoopbackCapture` (P/Invoke sendiri: `ActivateAudioInterfaceAsync` + `IAudioClient`, 48 kHz float stereo) menangkap audio WebView2. Sumber: nada sinus 440 Hz di halaman uji (`assets/tone.html`). Hasil: fase bersuara puncak -20,0 dBFS (sesuai gain), fase `IsMuted=true` benar-benar -120 dBFS (senyap), bersuara lagi setelah unmute. Meter per detik tercatat di log, WAV 10 dtk di `logs\s8-loopback.wav`.
  - **Temuan penting:** menargetkan **proses host aplikasi** dengan mode "include process tree" menghasilkan **senyap total**, padahal proses anaknya ada. Menargetkan **proses browser WebView2** (`CoreWebView2.BrowserProcessId`) berhasil, begitu juga proses `audio.mojom.AudioService` langsung. Dugaan (belum dibuktikan): mode tree hanya menjangkau satu tingkat anak. Desain: capture dibuat per proses browser WebView2, bukan per proses aplikasi. Satu environment/profil = satu proses browser, jadi tab yang berbagi profil tidak bisa dipisahkan audionya lewat loopback; pisahkan dengan `IsMuted` per tab (terbukti bekerja) atau beri environment terpisah.
  - Kontrol: menargetkan proses biasa yang memutar WAV tertangkap benar (-9 dBFS), jadi kode capture sendiri benar.
  - Sampel video uji (`bbb.mp4`) ternyata **tanpa trek audio**; audio video asli belum diuji. [Anda]: dengarkan WAV, dan cek video Canva/Kahoot yang berbunyi lewat `browse`.
  - Belum: format campuran (44,1 kHz/mono) lewat auto-convert hanya diuji pada 48 kHz stereo; latensi capture belum diukur.
- Implementasi capture WASAPI process loopback pada proses aplikasi (include process tree).
- Putar video berbunyi di WebView, tampilkan meter level dan simpan 10 detik ke WAV.
- Uji `IsMuted` pada WebView: apakah audio hilang dari loopback saat di-mute.
- **Lulus:** WAV berisi audio video yang jelas, dan mute bekerja. [Anda untuk mendengar, Saya untuk meter]
- **Risiko:** tidak ada wrapper siap pakai; kemungkinan P/Invoke sendiri.

### S9 - Dua WebView, pindah sumber
- **Status: lulus pada angka, [Anda] belum mendengar (2026-10-27).** Dua tab, masing-masing WebView2 composition dengan profil sendiri (proses browser sendiri), halaman merah+nada 440 Hz dan biru+nada 880 Hz. Satu visual "panggung" tetap berisi tab aktif (`Children.RemoveAll` + `InsertAtTop`), jadi **sesi capture tidak pernah dimulai ulang** saat pindah. Tab non-aktif di-mute (`IsMuted`). Empat kali pindah A-B-A-B, semuanya benar:
  - Gambar berganti dalam 32-44 ms (resolusi sampel 33 ms); audio: tab aktif -20,0 dBFS, tab non-aktif -120 dBFS (senyap) pada tiap fase, diukur lewat loopback terpisah per tab setelah 0,7 dtk.
  - **Beban tab latar:** tanpa menyembunyikan, tab non-aktif tetap memakai CPU ~3,5-5% (pohon proses browsernya, dari total semua inti) seperti tab aktif. Dengan `IsVisible=false` pada tab non-aktif turun ke 0,1-1,6%, tetapi pindah tab lebih lambat (58-119 ms). Keputusan desain: sembunyikan tab non-aktif secara bawaan (hemat), kecuali tab itu dipakai sebagai Preview (A3b), yang harus tetap tampil.
  - **Belum:** 0,7 dtk pertama setelah pindah tidak diukur ketat (kemungkinan ekor suara tab lama bertumpuk sesaat); halaman berat (Canva) belum dipakai; tab yang berbagi profil (satu proses browser) belum diuji; `IsVisible=false` mungkin menghentikan animasi/video di tab latar (memang diinginkan, tetapi kuis Kahoot/Slido di latar mungkin tidak ter-update) [U].
  - **[Anda]:** `scripts/run-spike.sh` lalu jalankan mode s9 untuk mendengar: nada 440 lalu 880 bergantian tanpa dobel. Perintah: `! cd C:\dev\stagecast\spikes\Stagecast.Spike\bin\Debug\net8.0-windows10.0.22621.0 && Stagecast.Spike.exe --mode s9 --autoclose` (PowerShell).
- Buat dua WebView (dua tab), capture hanya yang aktif, pindah dengan tombol.
- Tab non-aktif di-mute; ukur dampak CPU/GPU tab latar belakang.
- **Lulus:** pindah tab mengganti gambar dan audio tanpa error dan tanpa suara dobel. [Anda]

### S10 - Kontrol halaman dan tampilan diperbesar
- **Status: lulus untuk bagian [Saya] (2026-10-27); bagian [Anda] belum.** Diuji otomatis (`--mode s10`, halaman `assets/control.html`, pemetaan memakai `Core/Display/ViewTransform`, jadi kode yang sama dipakai aplikasi nanti):
  - **Akurasi klik:** Fit di kontrol 1280x720 (skala 0,508), 100% di tengah, 100% di pojok kanan-bawah; 21 titik. Selisih maksimum terhadap titik yang dituju 1,0 px (Fit), 0,57 px (100%); terhadap titik yang dikirim 0,00 px, jadi selisih hanya dari pembulatan mouse ke piksel penuh. Sembilan tombol tertuju semua dengan benar (selisih di bawah batas 2 px).
  - **Scroll roda** bekerja (scrollTop 300). **Keyboard** bekerja: keystroke Unicode asli lewat `SendInput` ke jendela aktif sampai ke kolom teks halaman ("stagecast 2520"); menjawab [U] keyboard. Jendela harus menjadi jendela aktif dan `MoveFocus` dipanggil.
  - **Zoom halaman** (`ZoomFactor`): 2,0 menjadikan viewport halaman 1260x540 (dpr 2), 0,5 menjadi 5040x2160; ukuran render tetap 2520x1080. Jadi zoom halaman mengubah isi output seperti diperkirakan.
  - **Kursor:** `CursorChanged` menyala dengan nilai kursor sistem (bentuk panah vs tangan), cukup untuk menggambar overlay. Menggambar overlay itu sendiri belum dibuat.
  - **Latensi klik ke frame:** 12/12 klik, minimum 38-39 ms, median 45 ms, maks 47 ms (frame dicuplik tiap 33 ms), dua run sama.
  - `FrameGate` (Freeze/Blackout) dan `ViewTransform` ada di Core dengan unit test; sink output nyata datang di fase A.
  - **Belum:** jendela preview terpisah, zoom tampilan dengan geser memakai mouse di UI nyata, Freeze/Blackout pada output sungguhan, Canva play/pause dan ganti slide dari preview [Anda]. Pointer ke piksel di DPI Windows 150% belum diuji.
Tujuan: halaman bisa dioperasikan dari preview yang diperbesar, bukan hanya dilihat.
- **Input ke halaman:** teruskan gerak mouse, klik, dan roda scroll dari preview ke WebView2 (`SendMouseInput`), dengan pemetaan koordinat: posisi di preview, dibagi skala tampilan, ditambah offset geser, menjadi piksel viewport.
- **Keyboard:** uji mengetik di kolom teks pada mode composition hosting. Apakah keyboard sampai ke halaman otomatis belum terbukti [U]; jika tidak, cari jalur fokus lain.
- **Zoom halaman** lewat properti zoom WebView2 [U: nama properti dan dampak ke capture diuji]. Ini mengubah isi output.
- **Zoom tampilan** (Fit / 100% / geser) hanya mengubah cara preview digambar; output tidak berubah.
- **Kursor:** frame capture tidak memuat kursor, jadi kursor digambar sebagai overlay; bentuk kursor dari event `CursorChanged` [V: event ada].
- **Jendela preview terpisah** yang bisa diubah ukurannya dan dipindah ke monitor kontrol.
- **Freeze dan Blackout:** sink menerima frame terakhir (atau hitam) sementara capture dan input halaman terus berjalan. Uji klik dan scroll saat Freeze aktif, lalu lepas Freeze dan pastikan keluaran langsung menampilkan keadaan terbaru.
- **Lulus:** pada tiga level zoom, klik tepat sasaran (selisih di bawah 2 piksel render) di halaman uji; scroll dan mengetik bekerja; play/pause video dan ganti slide di Canva bisa dilakukan dari preview. [Anda untuk Canva, Saya untuk akurasi koordinat]
- **Risiko:** jeda antara klik dan hasil terlihat (round trip lewat capture) mungkin terasa; ukur dalam milidetik.

### S11 - Skala anamorfik ke layar HDMI
- **Status: bagian [Saya] selesai secara offline; bagian [Anda] (LED/vMix/Resolume) belum (2026-10-27).** Laptop ini hanya punya satu layar 1920x1080, jadi jendela borderless di monitor eksternal dan filter WPF HighQuality/Direct3D **tidak** dibandingkan di sini. Yang dilakukan: frame capture nyata dari halaman pola (`assets/pattern.html`: garis 1 px, 1:2, 2 px, tepi tajam, teks 10-32 px, teks berwarna) diperas ke 1920 (lebar saja), dilebarkan kembali ke 2520 dengan bilinear dan bicubic, lima filter pemerasan dibandingkan (`AnamorphicSpike.cs`, resampling 1-D horizontal, 8-bit, ruang gamma seperti scaler GPU).
  - **Angka dari Core cocok:** peras lebar 76,19%, tinggi 100%; faktor pelebaran balik 131,25% (sama dengan `ScaleMath`, yang akan ditampilkan UI dan dimasukkan ke vMix/Resolume).
  - **Hasil (dilebarkan balik dengan bilinear / bicubic), PSNR teks dB:** nearest 24,8 / 25,3 (teks rusak dan garis alias/moire, dibuang); area 25,7 / 26,7; bilinear 25,6 / 26,7; **bicubic Catmull-Rom 26,4 / 27,7**; lanczos3 26,6 / 27,9. Garis 1 px menjadi abu-abu pada semua filter yang benar (33% bilinear/bicubic, 12% lanczos): 1920 piksel memang tidak bisa membawa detail 1 px dari 2520. Garis 2 px bertahan 85-98%. Lebar tepi 10-90%: ~2,3 px untuk semua filter yang benar.
  - **Rekomendasi sementara:** pemerasan **bicubic (Catmull-Rom)**: teks hanya ~0,2 dB di bawah Lanczos3, tanpa risiko ringing dan lebih murah di shader; Lanczos3 opsional. Bilinear (bawaan GPU) terlihat sedikit lebih lembut. Perbedaannya kecil (sekitar 1 dB); keputusan akhir tergantung penilaian mata Anda dan filter pelebaran balik di vMix/Resolume yang tidak kita kendalikan (bicubic memberi hasil sedikit lebih baik daripada bilinear di sisi balik).
  - **Saran isi konten:** teks 14 px ke atas pada render 2520 tetap terbaca; detail 1 px (garis tipis) akan memudar lewat jalur HDMI, tidak lewat NDI/OMT (2520 asli).
  - **[Anda]:** buka `...\logs\s11-compare.png` (baris per filter, kiri teks, kanan garis; diperbesar 2x tanpa penghalusan) dan nilai; lalu uji di LED lewat HDMI + vMix/Resolume pada fase integrasi. Gambar yang dikirim HDMI: `s11-squeezed-1920-area.png`.
Tujuan: memastikan gambar 2520x1080 yang diperas ke 1920x1080 masih tajam setelah diskalakan balik.
- Gambar frame capture ke jendela borderless di monitor 1920x1080 dengan skala non-seragam (lebar 76,2%, tinggi 100%).
- Bandingkan filter: bilinear, WPF HighQuality, dan filter lebih tajam (bicubic/Lanczos lewat Direct3D 11 atau shader) [U: pilihan mana yang layak diukur di sini].
- Uji dengan halaman berisi teks tipis, garis 1 piksel, dan tepi tajam; lalu skalakan lebar balik ke 131,25% (lewat vMix atau Resolume) dan nilai hasilnya.
- Catat kehilangan detail horizontal: HDMI 1920 piksel tidak bisa membawa 2520 piksel asli, jadi hasil akhir di LED akan sedikit lebih lunak dibanding jalur NDI/OMT yang mengirim 2520 asli.
- **Lulus:** ada filter yang hasil akhirnya dinilai cukup tajam oleh Anda di layar/LED target, dan faktor skala yang ditampilkan UI cocok dengan angka di vMix/Resolume. [Anda untuk ketajaman, Saya untuk angka]

### S12 - Preview dan program bersamaan
- **Status: lulus untuk bagian [Saya] (2026-10-27); kesan visual [Anda] belum.** Dua tab terlihat dan di-capture bersamaan, masing-masing di visual sendiri dan sesi capture sendiri. Program: tiap frame disalin penuh (2520x1080) ke CPU; preview: diperkecil di GPU (mipmap, 630x270) lalu disalin. Take hanya memindahkan peran program/preview, tidak ada sesi yang dimulai ulang. Dijalankan dengan dua proses browser terpisah dan dengan satu proses browser bersama (`--shared`), 15 dtk per fase:

  | Kondisi | fps program | frame masuk | interval >25 ms (dari ~900) | salinan penuh | CPU host + browser | GPU (iGPU AMD / GTX 1650) |
  |---|---|---|---|---|---|---|
  | satu tab (program saja) | 60,0 | 123-138/dtk | 5-11 | 5,9-6,6 ms | 4,4-4,6% + 3,4-3,7% | AMD 3D 21-23%; NVIDIA 3D 10-11%, copy 21-23% |
  | program + preview dioperasikan | 59,9-60,0 | 65-98/dtk | 85-128 | 5,3-7,0 ms | 4,2-5,4% + 5,3-7,8% | AMD 3D ~23%; NVIDIA 3D ~11%, copy ~25% |

  - **Preview diperkecil di GPU:** salinan CPU preview 1,4-1,6 ms rata-rata (maks 5-9 ms) dibanding 5,3-7 ms untuk salinan penuh; preview 15-60 fps tergantung `MinUpdateInterval` (tetap 60 fps bila tidak dibatasi).
  - **60 fps program bertahan saat preview aktif dan dioperasikan** (rata-rata 59,9-60,0), tetapi **kerataan turun**: laju frame compositor yang masuk jatuh dari ~130/dtk menjadi 65-98/dtk saat dua tab dirender, sehingga ~10% interval melebihi 25 ms (maks 34,7 ms) dibanding ~1% untuk satu tab. Membatasi capture preview ke 15 atau 30 fps tidak memperbaikinya jelas (jumlah frame masuk bervariasi antar run). Rata-rata aman, margin tipis. Sink output yang berdetak pada timer 60 Hz dan mengulang frame terakhir akan menyembunyikan ini; belum dibuktikan di LED.
  - **Audio tidak bocor, di kedua susunan:** program menangkap nada tab program 440 Hz = -20,0 dBFS dan nada tab preview 880 Hz (dimute) = -93,5 dBFS (proses terpisah) dan -92,4 dBFS (satu proses browser bersama). Artinya `IsMuted` per tab cukup; tab tidak perlu proses browser atau profil sendiri hanya demi audio. Mendengar audio preview (cue) tetap di luar v1.
  - **Take:** 6 kali bolak-balik di tiap susunan; frame baru pertama muncul di program dalam 6-26 ms, 0 frame hitam atau warna lain, tab lama tidak muncul lagi setelah pindah. Waktu itu tidak termasuk jalur kirim ke NDI/OMT/layar.
  - **GPU bawaan (tanpa paksaan):** aplikasi memakai kedua GPU, dengan salinan lintas-GPU (NVIDIA copy 21-25%). Konsisten dengan S7: paksa iGPU lewat Windows Graphics Settings.
  - **Belum:** halaman berat (Canva), dua tab dengan video H.264 sekaligus, konten >60 fps, beban saat NDI/OMT/layar penuh ikut berjalan, kesan visual Take [Anda].
Tujuan: membuktikan dua tab bisa dirender dan di-capture bersamaan untuk Preview + Take.
- Dua WebView2 di resolusi yang sama (misalnya 2520x1080), keduanya terlihat oleh compositor; satu sebagai program (ke FrameHub dan semua output), satu sebagai preview (hanya ke UI).
- Preview diperkecil di GPU sebelum disalin ke CPU, supaya salinan CPU hanya untuk jalur program. Ukur CPU, GPU, dan fps dibanding satu tab saja (S5).
- **Audio:** loopback proses mengambil seluruh pohon proses, sehingga suara tab preview akan masuk ke program bila tidak dibisukan. Uji `IsMuted` pada tab preview dan pastikan tidak ada bocor ke program. Mendengar audio preview (cue) tidak dijanjikan di v1: itu butuh proses terpisah per tab, yang berarti login terpisah [U].
- **Take:** tukar program dan preview; ukur waktu dari tekan TAKE sampai frame baru tampil di output, dan pastikan tidak ada kedip hitam.
- **Lulus:** 60 fps tetap stabil di program saat preview aktif dan dioperasikan; audio tab preview tidak bocor; Take terasa instan tanpa kedip. Bila beban terlalu besar, turunkan frame rate preview (misalnya 30 fps) dan catat sebagai batasan. [Saya untuk angka, Anda untuk kesan visual]

### GATE - Keputusan spike
- **Laporan:** `docs/SPIKE_REPORT.md` (2026-10-27). Rekomendasi saya: lulus dengan batasan, lanjut ke Bagian 2. Keputusan di tangan Anda.
Kumpulkan hasil S3-S12 jadi satu laporan pendek (angka fps, CPU/GPU, masalah). Keputusan:
- **Lulus:** lanjut ke Bagian 2.
- **Gagal sebagian:** catat batasan (misal maksimal 30 fps pada resolusi tertentu) dan putuskan menerima atau pindah jalur.
- **Gagal:** pindah ke jalur cadangan CEF dengan codec.

---

## BAGIAN 2 - APLIKASI

Dimulai dari proyek baru yang bersih; kode spike diambil sebagai referensi, bukan disalin mentah.

### A1 - Fondasi
- **Status: lulus (2026-10-27).** Struktur: `src/Stagecast.Core` (net8.0, murni, bisa diuji tanpa Windows: aturan, matematika skala, konfigurasi), `src/Stagecast.App` (WPF, `Stagecast.exe`, net8.0-windows10.0.22621.0, PerMonitorV2), `tests/Stagecast.Core.Tests`. Modul Windows (RenderEngine, audio) dan `Outputs` dibuat di fase yang memakainya (A2 dst), bukan sebagai proyek kosong sekarang. Konfigurasi: `config.json` di `%AppData%\Stagecast` (atau `--data-dir` / `STAGECAST_HOME`), ditulis lewat berkas sementara; nilai tak valid diperbaiki per bagian, JSON rusak disimpan sebagai `config.json.corrupt-<waktu>` lalu diganti bawaan, komentar dan koma akhir diizinkan, bidang tak dikenal diabaikan, preset kembar/tidak valid dibuang. Logging: Serilog ke `logs\stagecast-<tanggal>.log` (7 hari) lewat `Microsoft.Extensions.Logging`; pengecualian UI yang tak tertangani dicatat dan ditampilkan. Bukti: build bersih tanpa peringatan, 73 unit test lulus (13 baru untuk konfigurasi), aplikasi terbuka, membuat config bawaan, log "Config CreatedDefault 2520x1080 at 60 fps / Main window shown / exiting with code 0".
- Struktur solusi: `App` (WPF), `Core` (RenderEngine, FrameHub, AudioHub), `Outputs` (Display, NDI, OMT).
- Konfigurasi JSON dan logging.
- **Lulus:** build bersih, aplikasi terbuka, config terbaca. [Saya]

### A2 - RenderEngine + FrameHub
- Pindahkan hasil S2-S5 ke modul rapi.
- Input resolusi (W x H) dan toggle FPS 30/60 di UI; preview.
- FrameHub dengan ring buffer, pengulangan frame untuk halaman diam, statistik.
- **Lulus:** ubah resolusi/fps saat berjalan tanpa crash; statistik tampil. [Saya + Anda]
- **Status: bagian saya lulus (2026-10-27), menunggu pengecekan Anda.** Modul baru `src/Stagecast.Rendering` (RenderEngine, FrameCapturer WGC, FramePump) dan di Core `Frames/` (Frame berhitung referensi, FramePool, FrameHub). UI: kotak URL, input lebar x tinggi + Terapkan (divalidasi 100-8192), pilihan FPS 30/60, preview, panel statistik 1 Hz; resolusi dan FPS disimpan ke `config.json`. `--selftest a2` menjalankan skenario otomatis (dipakai `check.sh smoke`): 2520x1080@60 -> 30 fps -> 1920x1080@60 -> halaman diam -> kembali 2520x1080. Hasil: keluaran tepat 60,0/s dan 30,0/s di semua langkah, halaman diam tetap 60/s lewat pengulangan frame, ukuran frame mengikuti resolusi baru, tanpa error; 86 unit test lulus (13 baru untuk FrameHub/Frame/FramePool).
  - **Temuan:** `GraphicsCaptureItem.CreateFromVisual` menyimpan ukuran visual saat dibuat, jadi mengubah resolusi harus membuat ulang item/sesi tangkap (`FrameCapturer.Restart`); sesi baru langsung memberi satu frame walau halaman diam.
  - **Catatan untuk A4:** jadwal tangkap dan detak pompa adalah dua jam 60 Hz yang tidak terkunci, sehingga pada halaman bergerak jumlah detak yang mengulang frame bergantung pada fase (terlihat 3 sampai 119 dari 480 per 8 detik). Rata-rata tetap 60/s, tetapi gerakan bisa terlihat kurang halus; ukur lagi dengan keluaran nyata (layar penuh/NDI) sebelum memutuskan perlu penguncian fase.
  - **[Anda]:** jalankan `Stagecast.exe`, ketik resolusi lain, ganti 30/60, buka halaman (mis. Canva) dan lihat preview serta statistik.

### A2b - Kontrol halaman
- Toolbar di atas preview: Fit/100%, zoom halaman (di menu …), tombol Interaksi, Perbesar, jendela terpisah.
- Tombol Kembali/Maju/Muat ulang dan geser dengan Space + seret saat preview terpotong, plus mini-map posisi.
- Mode Perbesar menyembunyikan panel kanan; Blackout dan Freeze tetap terlihat.
- **Lulus:** semua kontrol halaman bekerja pada resolusi 2520x1080 dan resolusi lain; Esc melepas keyboard dari halaman. [Anda]
- **Status: bagian saya lulus (2026-10-27), menunggu pengecekan Anda.** Toolbar di atas preview: Kembali/Maju/Muat ulang, Fit/100%, menu `···` (zoom halaman 50-200%, label kuning bila bukan 100% karena ikut mengubah keluaran), Interaksi, Perbesar (menyembunyikan panel kanan), Jendela terpisah (preview pindah ke jendela sendiri dan kembali saat ditutup). Di 100% yang terpotong: seret (saat Interaksi mati) atau tahan Space lalu seret, plus mini-map yang bisa diklik/diseret. Badge "INTERAKSI · PREVIEW" (+ "KEYBOARD KE HALAMAN" saat keyboard ada di halaman); kursor mengikuti kursor halaman. Esc melepas keyboard dari halaman (lewat `AcceleratorKeyPressed`), mouse tetap jalan sampai Interaksi dimatikan.
  - **Preview kini mengambil bagian yang dibutuhkan saja:** Fit memakai tingkat mip terkecil yang tidak lebih kecil dari ukuran tampil (tanpa memperbesar); 100% memotong wilayah sebesar kontrol pada resolusi penuh. Salinan GPU dari frame terakhir disimpan supaya ganti tampilan pada halaman diam tetap menghasilkan gambar baru (tanpa itu preview membeku pada halaman yang tidak berubah; ditemukan lewat uji).
  - **Logika di Core (`PreviewView`, `PreviewRegion`), 16 tes unit baru** (102 total). `--selftest a2b` (bagian dari `check.sh smoke`): klik lewat jalur yang sama dengan mouse mendarat di titik yang dituju (Fit: galat terburuk 1,7 px pada skala 0,37, yaitu batas pembulatan satu piksel preview; 100%: 0,3-0,6 px, tombol salah 0), wilayah gambar preview sesuai permintaan, seret tidak keluar halaman, roda menggulir halaman, zoom halaman 2/0,5/1 mengubah viewport 1260/5040/2520, keyboard sampai ke halaman lalu Esc melepasnya, Kembali/Maju.
  - **[Anda]:** coba pada layar nyata: Interaksi di Canva (klik, gulir, ketik), Fit vs 100% pada beberapa resolusi (termasuk yang lebih kecil dari preview), seret + Space + mini-map, Perbesar, Jendela terpisah di monitor kedua, Esc. Belum diuji otomatis: Space+seret dan seret mouse sungguhan, kursor, skala layar 125/150%.

### A2c - Konfirmasi perubahan (tanpa mode kunci)
- Tidak ada mode Setup/Show (keputusan 2026-10-27). Perlindungan lewat konfirmasi kontekstual, seperti tabel di `PLAN_UI.md` ("Perlindungan perubahan").
- Ubah resolusi atau FPS saat ada output nyala: spanduk konfirmasi Batal/Terapkan. Resolusi yang diketik berlaku setelah Terapkan.
- Matikan output selalu minta konfirmasi; test tone ke NDI/OMT yang nyala minta konfirmasi.
- **Lulus:** ubah resolusi/FPS saat live tidak berlaku sebelum dikonfirmasi; mematikan output tidak terjadi dengan satu klik tak sengaja; menyalakan output tidak minta konfirmasi. [Anda]
- **Status: sebagian selesai (2026-10-27).** Selesai: spanduk kuning Batal/Terapkan untuk resolusi dan FPS (`RenderChangeGate` di Core, 8 tes). Resolusi dan FPS yang diminta bersamaan menunggu bersama; meminta nilai yang sedang berlaku menarik kembali bagian itu; kontrol (pilihan FPS, tombol preset) menampilkan nilai yang menunggu, spanduk menampilkan nilai lama dan baru. **Belum bisa diuji dengan output sungguhan** karena output baru ada di A4: sementara ada kotak centang "Uji: anggap ada output yang nyala" di panel kanan (hapus di A4). Konfirmasi matikan output dan test tone ke NDI/OMT dikerjakan bersama fasenya (A4, A6, A7, A8).

### A2d - Preset resolusi
- Lebar/tinggi bisa diketik (validasi 100-8192 piksel, angka batas ini usulan untuk mockup, belum diuji terhadap batas WebView2/GPU [U]).
- **Simpan preset:** menyimpan resolusi yang sedang dipakai dengan nama (maksimal 24 karakter, tidak kosong, tidak kembar nama atau resolusi).
- **Hapus preset:** lewat mode Kelola; preset bawaan (LED 7x3, Full HD, 4K UHD, Vertikal) tidak bisa dihapus; konfirmasi sebelum hapus; menghapus preset yang sedang dipakai tidak mengubah resolusi aktif.
- Preset disimpan di profil JSON. Menerapkan resolusi baru meminta konfirmasi bila output nyala (A2c).
- **Lulus:** preset buatan sendiri bertahan setelah aplikasi dibuka ulang. [Anda]
- **Status: bagian saya lulus (2026-10-27), menunggu pengecekan Anda.** Tombol "Preset: <nama>" (atau "Kustom") membuka daftar preset; memilih preset melewati spanduk A2c yang sama. "Simpan preset" menyimpan resolusi yang sedang dipakai (nama dipangkas, maks 24 karakter, tidak kosong, tidak kembar nama tanpa memandang huruf besar/kecil, tidak kembar resolusi; pesan galat per kasus). "Kelola" menampilkan daftar dengan Hapus (preset bawaan diberi tanda "bawaan", tanpa tombol), meminta konfirmasi, dan tidak mengubah resolusi aktif. Disimpan di `config.json` lewat `PresetCatalog` (Core, 12 tes); entri rusak/kembar saat memuat dibuang. `--selftest a2c` (bagian dari `check.sh smoke`) mencakup banner dan preset, termasuk bahwa berkas config berisi preset (bertahan setelah dibuka ulang) dan hilang setelah dihapus. 122 tes unit.
  - **[Anda]:** simpan preset sendiri, tutup lalu buka lagi aplikasinya, cek masih ada; coba kotak centang uji untuk melihat spanduk.

### A3 - Multi-tab
- Daftar tab: tambah, tutup, ganti nama (pensil di baris tab, F2), urut; pindah tab mengganti sumber. Nama tab hanya label operator, disimpan di profil, tidak boleh kosong, maksimal 40 karakter.
- Tahan frame terakhir sampai frame pertama tab baru.
- **Lulus:** 5 tab berjalan, pindah-pindah tanpa kedip hitam. [Anda]
- **Status: bagian saya lulus (2026-10-27), menunggu pengecekan Anda.** Mesin: `RenderEngine` kini memegang banyak `PageTab` (WebView2 composition + visual sendiri, satu profil bersama); visual "panggung" tetap dan satu sesi capture, tab baru tidak memulai ulang capture. Daftar tab di Core (`TabList`, `TabRules`): tambah (maks 12), tutup (tab terakhir tidak bisa ditutup; menutup tab aktif mengaktifkan tetangganya), ganti nama (dipangkas, tidak kosong, maks 40 karakter), urut naik/turun (tab aktif tetap aktif), nama bawaan "Tab N" memakai nomor kosong pertama. UI: daftar di panel kanan (penanda TAYANG pada tab aktif, pensil/F2, ▲ ▼, ✕), Ctrl+T, Ctrl+W, Ctrl+1-9. Tab dan tab aktif disimpan di `config.json` (nama + alamat saja; halaman `data:` disimpan sebagai kosong); popup/`target=_blank` dibuka di tab yang sama (dari S6).
  - **Temuan penting (jangan diulang):** (1) menyembunyikan WebView tab latar (`IsVisible=false`) lalu menampilkannya lagi membuat halaman yang **diam** tetap kosong/hitam dan tidak ada frame baru yang datang; S9 lolos hanya karena halaman ujinya terus berdenyut. Karena itu WebView tab latar **tidak disembunyikan**; yang dimatikan hanya visual compositionnya dan tab dibisukan. Konsekuensi: tab latar tetap memakai CPU seperti S9 (sekitar 3,5-5% per tab pada halaman uji; halaman berat belum diukur [U]). (2) Menambah/mencabut visual dari panggung saat capture menghasilkan frame hitam; visual semua tab tetap menempel di panggung dan yang berganti hanya `Visual.IsVisible`. (3) Menyalakan visual baru langsung di atas masih memberi satu frame hitam per pindah; solusinya **tumpang tindih**: visual baru dinyalakan di bawah yang lama, tunggu 80 ms agar compositor menggambarnya, baru dinaikkan dan yang lama dimatikan.
  - **Hasil uji otomatis (`--selftest a3`, masuk `check.sh smoke`):** 5 tab warna berbeda, 16 perpindahan acak: **0 frame hitam sampai ke keluaran**, warna benar setiap kali, latensi pindah median 113 ms (maks 119 ms), hanya tab aktif yang tidak dibisukan; ganti nama (kosong dan >40 ditolak), urut, tutup tab aktif tanpa kedip, batas 12 tab, tab terakhir tak bisa ditutup, berkas config berisi tab. Frame hitam hanya muncul sekali saat halaman pertama dimuat sesudah aplikasi baru jalan (bukan pindah tab).
  - **[Anda]:** buka 4-5 tab sungguhan (Canva, Kahoot, Slido, MP4) dan pindah-pindah sambil melihat preview; perhatikan CPU di panel statistik/Task Manager dengan banyak tab latar.

### A3c - Sesi login
- Folder data WebView2 permanen (`%LocalAppData%`), dipakai bersama semua tab; opsi per tab "Profil terpisah" untuk akun berbeda pada situs yang sama (lebih berat).
- Penanganan popup login: jendela kecil sementara, atau tab yang sama; tutup otomatis setelah login selesai.
- Aplikasi tidak menyimpan password; profil JSON tidak berisi kredensial. Folder data dianggap sensitif.
- Tombol **Hapus data login** dengan konfirmasi.
- **Cek sesi sebelum acara:** buka tiap tab, tandai SESI AKTIF / PERLU LOGIN / TANPA LOGIN. Deteksi "perlu login" heuristik (pola URL halaman login) [U]; operator tetap memeriksa di preview.
- Anjuran UI: login di tab Preview; di mode Langsung sarankan Freeze dulu agar ketikan tidak tampil di layar.
- **Lulus:** login sekali bertahan setelah restart; popup Google berhasil; Hapus data login benar-benar keluar dari semua situs. [Anda]

### A3b - Preview + Take
- Saklar Langsung / Preview + Take di atas daftar tab; tombol TAKE (tukar program dan preview), pil LIVE/PREVIEW, kotak PROGRAM di pojok preview.
- Interaksi mengarah ke tab preview; tab preview selalu dibisukan; audio hanya dari program.
- Freeze/Blackout memengaruhi program saja; kotak PROGRAM menampilkan gambar beku atau hitam. Tab program tidak bisa dioperasikan dari mode ini (hanya dilihat).
- Bergantung pada hasil S12; bila beban terlalu berat, preview diturunkan ke 30 fps.
- **Lulus:** menyiapkan tab lain di preview tidak terlihat di output, TAKE tanpa kedip, dan tidak ada suara bocor. [Anda]

### A4 - Display output
- Daftar monitor, pilih monitor, fullscreen borderless, mode skala **Peras (anamorfik, utama)** / Fit / 1:1, per-monitor DPI aware.
- Peras hanya untuk output layar; NDI/OMT (A6, A7) mengirim frame native tanpa skala.
- Peras: skala horizontal dan vertikal berbeda (2520x1080 ke 1920x1080 = lebar 76,2%, tinggi 100%). UI menampilkan faktor skala dan angka yang harus diisi di Resolume/vMix (131,25%).
- Kualitas filter diputuskan dari hasil S11.
- **Selalu di atas** (opsi, default nyala): jendela output ditandai topmost sehingga menutupi jendela lain di monitor itu, termasuk bar tugas. Pengaturan ini ada di kartu Layar penuh.
  - Jendela output tidak merebut fokus keyboard saat tampil (`WS_EX_NOACTIVATE` atau setara) [U: diuji].
  - Topmost biasa tidak menutupi aplikasi exclusive-fullscreen atau overlay tertentu dari driver GPU/Windows [U: diuji di mesin target].
  - Pengaman: bila hanya ada satu monitor atau monitor output adalah monitor kontrol, aplikasi memperingatkan karena output yang selalu di atas dapat menutupi jendela kontrol dan mengunci operator.
  - Tidak ada cara bawaan Windows untuk menurunkan jendela topmost dari luar, jadi sediakan pintasan global darurat untuk menyembunyikan output [U: pintasan dan bentrok dengan aplikasi lain diuji].
- Tangani monitor dicabut/dicolok.
- **Lulus:** tampil tepat di monitor pilihan; uji di layar extended nyata. [Anda]

### A5 - Audio pipeline
- AudioHub dari S8: volume, mute, meter.
- Perangkat audio lokal pilihan (WASAPI), resampling, sinkronisasi dengan clock video.
- **Freeze membisukan audio keluaran** (senyap, bukan diulang); kembali live saat dilepas. Blackout tidak memengaruhi audio.
- **Test tone:** generator di AudioHub (sinus 1 kHz atau pink noise, -20 dBFS, kanal L+R/Kiri/Kanan) yang menggantikan audio halaman selama aktif. Target dipilih: Lokal, NDI, OMT. Berhenti otomatis setelah 30 detik. Memulai tone ke NDI/OMT yang sedang nyala meminta konfirmasi, agar tone tidak bocor ke feed program tanpa sengaja. Kanal kiri/kanan berguna untuk mengecek urutan stereo di vMix atau mixer.
- **Lulus:** suara keluar di perangkat yang dipilih, tanpa dobel, lip-sync wajar; tone terdengar di kanal yang benar pada perangkat lokal, NDI Monitor, dan vMix. [Anda]

### A6 - NDI output
- P/Invoke tipis ke runtime NDI Basic; kirim BGRA + audio float.
- Nama sumber bisa diubah dari UI dan disimpan di profil. NDI memakai nama saat sender dibuat, jadi mengubah nama saat output nyala berarti sender ditutup dan dibuat ulang; UI memberi peringatan. Validasi: tidak kosong, batas panjang sesuai dokumentasi NDI [U].
- Atribusi ndi.video di UI/About.
- **Lulus:** sumber muncul di vMix atau NDI Monitor dengan gambar dan suara benar. [Anda]

### A7 - OMT output
- Pakai `libomtnet` (MIT) + `libvmx.dll`; kirim BGRA + audio float.
- Pelajari discovery di Windows saat mulai tahap ini.
- Nama sumber OMT bisa diubah dari UI dan disimpan di profil; perilaku mengubah nama saat nyala diuji seperti pada NDI (A6).
- **Lulus:** sumber OMT terdeteksi di vMix 29+ dengan gambar dan suara benar. [Anda]

### A8 - Stabilitas dan uji beban
- Profil/tab tersimpan dan auto-start; pemulihan saat jaringan putus.
- Uji beban 2520x1080@60 dengan semua output aktif selama beberapa jam; catat memori dan frame dibuang.
- **Lulus:** tanpa crash, tanpa kebocoran memori yang berarti. [Saya + Anda]

### A9 - Packaging
- Folder portable atau installer (Inno Setup); sertakan DLL NDI dan libvmx, notice lisensi (NDI, OMT, dll).
- **Lulus:** terpasang dan berjalan di mesin Windows 11 bersih. [Anda]

---

## Ringkasan urutan

S0 -> S1 -> S2 -> S3 -> S4 (titik keputusan awal) -> S5 -> S6 -> S7 -> S8 -> S9 -> S10 -> S11 -> S12 -> GATE -> A1, A2, A2b, A2c, A2d, A3, A3c, A3b, A4 ... A9

Titik di mana saya akan berhenti dan meminta Anda melihat/mendengar langsung: S1, S4, S5, S6, S8, S9, lalu setiap tahap A4-A7.
