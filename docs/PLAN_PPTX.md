# Rencana: sumber PowerPoint (PPTX) di Stagecast

Status: rencana, belum dikerjakan. Branch: `custom`. Belum ada kode yang diuji.

## Pertanyaan

Bisakah Stagecast memutar file `.pptx` yang berisi slide gambar, animasi teks, dan video MP4, dengan memakai PowerPoint Slide Show?

## Jawaban singkat

Bisa, dengan syarat PowerPoint terpasang di PC operator. Caranya: Stagecast menjalankan Slide Show lewat PowerPoint sendiri (COM automation), menangkap jendela slide show, lalu mengirim gambarnya ke jalur output yang sudah ada (layar penuh, NDI, OMT). Animasi, transisi, dan video diputar oleh PowerPoint, jadi hasilnya sama dengan yang dilihat di PowerPoint. Stagecast tidak perlu merender animasi sendiri.

## Opsi yang dipertimbangkan

| Opsi | Cara | Kelebihan | Kekurangan |
|---|---|---|---|
| **A. PowerPoint Slide Show + capture jendela (disarankan)** | COM mengontrol PowerPoint, Windows Graphics Capture menangkap jendela | Animasi, transisi, video, dan font persis seperti aslinya | Butuh PowerPoint berlisensi; hanya Windows; PowerPoint ikut berjalan |
| B. Baca PPTX sendiri (Open XML) lalu render | Parser dan renderer sendiri | Tanpa PowerPoint | Animasi dan transisi hampir mustahil ditiru setia; font dan efek sering beda; kerja sangat besar |
| C. Ekspor lewat PowerPoint ke gambar atau MP4 | `Export` atau `CreateVideo` | Sederhana; bisa diputar tanpa PowerPoint saat acara | Animasi per klik hilang; tidak bisa maju-mundur live; ekspor lama |
| D. LibreOffice headless | Konversi | Gratis | Animasi dan video tidak setia |

Rekomendasi: **A** untuk pemutaran live. **C** bisa jadi fitur tambahan nanti ("siapkan sebagai MP4") untuk PC tanpa PowerPoint.

## Rancangan opsi A

### Jenis sumber baru
Tab sekarang selalu berupa halaman web (WebView2). Tambahkan jenis tab "PowerPoint" yang memiliki:
- path file `.pptx` (disimpan di konfigurasi sebagai path saja, tanpa isi file),
- status: siap, memutar, selesai, atau error,
- nomor slide dan jumlah slide saat ini.

Bagian yang dipakai ulang tanpa diubah: `FrameHub` dan gerbangnya (Live/Freeze/Blackout), pilihan resolusi render, output layar penuh dengan squeeze anamorfik, NDI, OMT, dan `AudioHub`.

### Menjalankan PowerPoint
- `PowerPoint.Application` lewat COM late-binding, supaya tidak ada pustaka Office yang ikut dikemas atau didistribusikan.
- Buka presentasi `ReadOnly` dan `WithWindow:=False`. Pakai salinan sementara di folder milik Stagecast supaya file asli tidak terkunci atau berubah.
- `SlideShowSettings.ShowType = ppShowTypeWindowed` (bukan layar penuh), `ShowPresenterView = False`, `ShowWithAnimation = True`, `LoopUntilStopped` sesuai pilihan operator.
- Ukuran jendela: atur `SlideShowWindow.Width/Height` (satuan point) dan posisinya. Jendela ditempatkan di monitor operator, bukan di layar videotron.

### Rasio slide dan resolusi (keputusan: scale to fit)
Keputusan pemilik: file PPTX tidak diubah sama sekali, juga untuk kanvas videotron 7×3 m (2520×1080). Satu-satunya perlakuan adalah **scale to fit**.

- Rasio slide (4:3, 16:9, atau ukuran khusus) dibaca dari `PageSetup.SlideWidth/Height`. Ukuran slide di file tidak pernah diubah dan file asli tidak pernah ditulis.
- Slide diperbesar sampai salah satu sisinya mentok di kanvas render, rasionya dijaga, dan sisanya diisi hitam (letterbox atau pillarbox). Tidak ada mode isi penuh atau regang.
- Contoh: 16:9 di 2520×1080 menjadi 1920×1080 di tengah (bar 300 px kiri dan kanan). 4:3 di 2520×1080 menjadi 1440×1080 (bar 540 px). Di kanvas 1920×1080, 4:3 menjadi 1440×1080 (bar 240 px).
- Jendela slide show diatur seukuran area hasil fit (misalnya 1440×1080 untuk slide 4:3), supaya tangkapan sudah sesuai ukuran akhir dan tidak diperbesar (tidak buram). Penempatan ke tengah kanvas dilakukan Stagecast, bukan PowerPoint.
- Squeeze anamorfik tetap terpisah dan hanya di keluaran layar penuh. NDI dan OMT mengirim kanvas ukuran asli (termasuk bar hitamnya).
- Perlu dibuktikan di spike P0: jendela slide show seukuran area fit pada DPI 100% dan 150%, dan piksel hasil tangkapan sama persis dengan ukuran jendela.

### Opsi alpha untuk NDI dan OMT
Status: rancangan, belum dibuat, belum diuji dengan vMix atau Resolume.

- Sekarang NDI mengirim BGRX dan OMT mengirim BGRA tanpa flag alpha, jadi keduanya opaque (lihat `NdiOutput.cs` dan `OmtOutput.cs`). Frame di dalam aplikasi sudah BGRA, jadi satu kanal alpha sudah ada di jalurnya.
- Tambah opsi **"Kirim alpha"** terpisah untuk NDI dan untuk OMT, bawaan mati. Saat menyala, NDI mengirim BGRA dan OMT memasang flag alpha.
- Untuk tab PowerPoint: Stagecast sendiri yang menyusun kanvas scale to fit, jadi bar letterbox/pillarbox menjadi transparan (alpha 0) dan isi slide opaque. Receiver bisa menumpuknya di atas video lain tanpa bar hitam.
- Keluaran layar penuh tidak mendukung alpha: bar tetap hitam.
- Untuk tab web: alpha hanya berguna kalau halaman berlatar transparan. Apakah tangkapan WebView2 mempertahankan alpha belum diketahui; dicoba di spike, bukan dijanjikan.
- Hal yang harus dibuktikan: alpha straight atau premultiplied untuk tiap protokol dan apa yang diharapkan vMix/Resolume (kalau salah, tepi tampak berhalo hitam); vMix menerima alpha dari OMT; beban CPU dan bandwidth lebih besar; penyimpanan opsi hanya status pilihan, bukan on/off output (output tetap manual).
- Keputusan pemilik: Blackout mengirim hitam opaque penuh (alpha 255), juga saat alpha menyala. Bar scale to fit pada tab PowerPoint tetap transparan selama Live. Freeze menahan frame terakhir beserta alphanya.

### Menangkap gambar
- `FrameCapturer` sekarang menangkap visual komposisi WebView2. Tambahkan sumber capture kedua yang memakai `GraphicsCaptureItem` dari `HWND` jendela slide show (Windows Graphics Capture, API yang sama).
- Keluarannya dimasukkan ke `FrameHub` dengan jalur yang sama, jadi FPS 30/60, freeze, blackout, preview, dan thumbnail tetap bekerja.
- Jendela tidak boleh diminimalkan. Jendela yang tertutup jendela lain tetap bisa ditangkap oleh WGC, tetapi harus diuji pada PowerPoint yang sebenarnya.
- Hindari border kuning pada jendela yang ditangkap: `IsBorderRequired = false` (Windows 11).

### Audio
- Video MP4 dan suara di dalam slide diputar oleh proses PowerPoint (`POWERPNT.EXE`).
- `ProcessLoopbackCapture` yang sudah ada menerima id proses, jadi bisa dipakai untuk `POWERPNT.EXE`. Catatan dari spike S8: menarget proses yang salah bisa diam, jadi proses yang benar perlu dicari dan diuji.
- Freeze membisukan audio dan Blackout tidak, sama seperti tab web.

### Kontrol
Dari panel tengah Stagecast: Slide berikutnya/sebelumnya (`View.Next/Previous`), langsung ke slide N (`GotoSlide`), mulai dari awal, jeda animasi, dan akhiri show. Pintasan keyboard dan klik pada pratinjau diteruskan sebagai `Next`. Status "slide 3/12" ditampilkan di tab dan di header.

Tidak ada penerusan keyboard atau mouse mentah ke jendela PowerPoint, supaya tidak ada klik nyasar ke jendela lain.

### Strip thumbnail slide
Status: rancangan, belum dibuat. Mockup ada di artifact storyboard.

- Deretan thumbnail slide tepat di bawah pratinjau, hanya tampil saat tab PowerPoint menjadi tab yang dioperasikan.
- Gambar dibuat PowerPoint saat persiapan (`Slide.Export` ke PNG, satu per slide, ukuran kecil), disimpan di folder sementara Stagecast dan dihapus saat tab ditutup. Gambarnya diam, tanpa animasi.
- Slide yang tayang diberi bingkai merah, slide berikutnya biru. Penanda: VIDEO (dengan durasi, frame poster), ANIM, TERSEMBUNYI.
- Klik thumbnail memanggil `GotoSlide`. Strip bergulir mengikuti slide yang tayang, juga saat slide digerakkan clicker.
- Belum ada di tab web dan MP4. Di Preview + Take, strip tampil untuk tab PowerPoint yang dioperasikan.
- Hal yang harus dibuktikan: lama ekspor seluruh slide pada deck besar, dan apakah `Slide.Export` aman dipanggil selagi show berjalan. Dimasukkan ke spike P0 dan fase P3.

### Slide tersembunyi (hidden)
Status: rancangan, belum dibuat, belum diuji. Perilaku PowerPoint di bawah ini adalah yang saya ketahui dari PowerPoint biasa dan harus dibuktikan di spike P0 lewat COM.

- Slide dijalankan oleh PowerPoint sendiri, jadi slide yang disembunyikan di file ikut dilewati oleh Berikutnya, Sebelumnya, dan clicker.
- **Mulai dan akhir show (keputusan pemilik):** kalau slide pertama atau terakhir tersembunyi, show mulai di slide tampil pertama dan berakhir di slide tampil terakhir. Slide tersembunyi pertama tidak pernah ditayangkan saat show dimulai. "Mulai dari awal" mengikuti aturan yang sama.
- **Strip thumbnail:** slide tersembunyi tetap ditampilkan dengan bingkai putus-putus, redup, dan label TERSEMBUNYI.
- **Loncat ke slide tersembunyi (keputusan pemilik):** boleh, dengan satu konfirmasi kecil di dalam aplikasi ("Slide ini disembunyikan. Tayangkan?"), karena slide itu mungkin sengaja disembunyikan. Apakah `GotoSlide` lewat COM bisa menuju slide tersembunyi belum dipastikan.
- **Penomoran:** "Slide 3 / 12" memakai nomor asli di file dan menghitung slide tersembunyi.
- **Belum diketahui:** apakah `Slide.Export` menghasilkan thumbnail untuk slide tersembunyi sama seperti slide biasa (pemilik belum mencoba; diuji di P0). Kalau gagal, thumbnail slide tersembunyi diganti kotak abu-abu bertuliskan nomor dan TERSEMBUNYI.
- **Tambahan uji P0:** deck uji berisi slide tersembunyi di awal, di tengah, dan di akhir; periksa Berikutnya/Sebelumnya, mulai dari awal, akhir show, loncat ke slide tersembunyi, dan ekspor thumbnail-nya.

### Hasil tinjauan celah (2026-10-27)
Status: rancangan, belum dibuat, belum diuji.

**Diputuskan pemilik**
- **Maju slide hanya manual.** Waktu maju otomatis di file diabaikan; slide hanya berpindah lewat perintah operator atau clicker. Diterapkan lewat pengaturan show (`AdvanceMode` manual). Yang diabaikan hanya pergantian slide otomatis. Animasi di dalam satu slide yang diatur "setelah sebelumnya" dan video yang otomatis mulai tetap berjalan; perlu dibuktikan di P0 bahwa pengaturan itu memang tidak mematikannya.
- **Muat ulang deck.** Kalau file asli diubah setelah dibuka (misalnya diedit menit terakhir), tombol "Muat ulang" membuka salinan baru dan kembali ke nomor slide yang sama. Tidak ada deteksi otomatis. Asumsi saya: kalau tab sedang program, muat ulang meminta konfirmasi di dalam aplikasi.
- **Jenis file:** versi pertama hanya `.pptx` (jenis lain tidak umum bagi pemilik). `.pptm` dan `.ppsm` tetap ditolak.
- **Tanpa layar panggung:** tidak ada tampilan catatan pembicara, timer, atau presenter view.
- **Tanpa pintasan keyboard tambahan** untuk Take, Freeze, atau pindah tab. Pintasan yang sudah ada dan pemetaan clicker tidak berubah.
- **Video dalam slide:** ikut codec yang didukung PowerPoint; tidak ada peringatan format tambahan. Peringatan media tertaut yang sudah direncanakan tetap.
- **Kursor mouse harus benar-benar tidak muncul di output.** Penangkapan jendela PowerPoint mematikan kursor seperti penangkapan halaman web yang sekarang (`IsCursorCaptureEnabled = false`), dan ini diuji sungguhan: gerakkan mouse di atas jendela show, lalu periksa frame output. Termasuk periksa toolbar kecil PowerPoint yang bisa muncul saat mouse bergerak di jendela show dan menu klik kanan; keduanya tidak boleh tampil di output. Cara menyembunyikan toolbar itu lewat COM belum diketahui.

**Usulan saya, belum dijawab pemilik**
- **Jangan menutup PowerPoint milik pengguna.** Kalau PowerPoint sudah terbuka, Stagecast memakai instance itu; saat selesai hanya presentasi kita yang ditutup, dan PowerPoint hanya ditutup bila Stagecast sendiri yang menjalankannya. Perlu diuji saat PowerPoint juga dipakai mengedit deck lain.
- **Peringatan font tidak terpasang** saat persiapan: periksa font yang dipakai deck terhadap font di PC acara, tampilkan peringatan seperti peringatan media tertaut.

**Berlaku global (bukan hanya PPTX), belum dijawab pemilik**
- **Tahan tidur:** selama ada output yang menyala, cegah PC tidur atau layar mati. Di kode tidak ditemukan penahan tidur.
- **Konfirmasi saat menutup aplikasi** ketika ada output menyala. Di kode tidak ditemukan konfirmasi keluar; risikonya naik setelah tombol tutup pindah ke top bar.

### Clicker (presentation remote)
Status: rancangan, belum dibuat, belum diuji dengan clicker sungguhan.

- Kebanyakan clicker adalah keyboard USB (HID): maju = Page Down / panah kanan, mundur = Page Up / panah kiri, sebagian punya layar hitam (B atau titik) dan mulai/keluar (F5, Esc). Clicker dengan tombol atau perangkat lunak khusus yang tidak mengirim tombol keyboard standar tidak akan terbaca.
- Tombol diterima Stagecast sebagai perintah, bukan diteruskan mentah ke PowerPoint: Berikutnya, Sebelumnya, Blackout, dan Mulai dari awal. Pemetaan tombol bisa diubah di pengaturan karena merek clicker berbeda.
- **Pemetaan bawaan (clicker umum, mis. kelas Logitech R400/R500 atau Kensington; belum diuji):** Page Down, panah kanan, spasi, dan Enter = Berikutnya. Page Up, panah kiri, dan Backspace = Sebelumnya. B atau titik = Blackout. F5 = Mulai dari awal. Esc tidak diberi fungsi, supaya show tidak berakhir tanpa sengaja.
- **Mode fokus (bawaan):** tombol hanya bekerja saat jendela Stagecast aktif.
- **Mode global (opsional, dinyalakan operator sendiri):** tombol tetap bekerja walau jendela lain aktif, tetapi hanya saat tab PowerPoint menjadi program. Selama aktif, header menampilkan lencana "CLICKER AKTIF". Risikonya: tombol Page Down tidak sampai ke aplikasi lain di PC itu. Mode ini mati otomatis saat tab PowerPoint tidak lagi menjadi program.
- **Mencegah slide maju dua kali:** kalau jendela PowerPoint sampai mengambil fokus, clicker menggerakkannya langsung dan Stagecast ikut menanganinya. Jendela slide show tidak boleh mengambil fokus (dibuka tanpa aktivasi), dan Stagecast mengabaikan tombol yang datang saat jendela PowerPoint yang aktif. Harus diuji di spike P0.
- Jalur cadangan: MP4 memetakan maju ke putar/jeda dan tidak ada slide; tab web meneruskan tombol lewat mode Interaksi yang sudah ada (belum diuji).
- Tombol hanya memicu perintah kontrol. Tidak ada tombol clicker yang menyalakan atau mematikan output (layar penuh, NDI, OMT tetap manual).

### Preview + Take (keputusan: tab PowerPoint boleh jadi preview maupun program)
Status: rancangan, belum dibuat, belum diuji. Mockup ada di artifact storyboard.

- **Tab PowerPoint sebagai preview, tab lain (web atau MP4) sebagai program:** slide show disiapkan di preview. Operator memilih slide awal lewat strip thumbnail, deck dibuka pada slide itu, dan gambarnya tampil di panel preview tanpa masuk ke output. Take memindahkan sumber program ke tab PowerPoint tanpa memulai ulang show. Tab program sebelumnya menjadi preview.
- **Tab PowerPoint sebagai program:** next, previous, loncat slide, dan clicker tetap bekerja. Ini pengecualian dari aturan "tab program tidak bisa dioperasikan": perintah slide memang tujuan tab ini, dan tidak ada klik atau tombol mentah ke halaman. Ini asumsi saya; perlu dikonfirmasi pemilik.
- **Clicker memilih target:** kalau ada tab PowerPoint di program, clicker menggerakkan itu. Kalau tidak, clicker menggerakkan tab PowerPoint yang sedang di preview. Kalau dua-duanya PowerPoint, clicker hanya menggerakkan yang program; preview digerakkan lewat tombol di layar dan strip.
- **Audio:** audio `POWERPNT.EXE` hanya diteruskan ke NDI, OMT, dan perangkat pilihan saat tab PowerPoint menjadi program. Saat preview, Stagecast tidak meneruskannya. Catatan: PowerPoint tetap memutar suara ke perangkat bawaan Windows PC itu, jadi video di preview bisa terdengar di speaker lokal. Perlu diuji dan mungkin perlu peringatan atau menutup suara saat preview.
- **Dua tab PowerPoint sekaligus (satu program, satu preview):** belum diketahui apakah satu PowerPoint bisa menjalankan dua slide show bersamaan. Diuji di spike P0. Kalau tidak bisa, preview tab PowerPoint kedua hanya berupa gambar diam (thumbnail dan strip), dan show-nya baru dimulai saat Take. Dua instansi PowerPoint tidak dipakai karena rapuh.
- Strip thumbnail tampil untuk tab PowerPoint yang sedang dioperasikan, di mode Langsung maupun Preview + Take.

### Konfigurasi dan keamanan
- Yang disimpan hanya path file dan opsi. Isi presentasi tidak disalin ke konfigurasi.
- File PPTX adalah input yang tidak tepercaya. Makro dimatikan (buka sebagai `.pptx` tanpa makro; tolak `.pptm`/`.ppsm`), `Application.AutomationSecurity = msoAutomationSecurityForceDisable`.
- Jangan simpan kata sandi file; kalau PPTX terenkripsi, minta operator membuka dan menyimpan ulang.

## Fase kerja

Semua fase di bawah baru rencana. Tidak ada yang boleh ditandai lulus sebelum diuji (aturan proyek).

| Fase | Isi | Cara menyatakan lulus |
|---|---|---|
| P0 Spike | Skrip kecil: buka PPTX dengan COM, mulai slide show windowed, tangkap `HWND` dengan WGC, simpan beberapa frame ke PNG; ukur ukuran piksel jendela, FPS, dan apakah video mp4 dan animasi muncul. Juga uji: pengaturan maju manual (waktu otomatis diabaikan, animasi "setelah sebelumnya" dan video otomatis tetap jalan), kursor dan toolbar PowerPoint tidak ikut tertangkap, dan slide tersembunyi (di awal, tengah, akhir; lompatan dan ekspor thumbnail) dan fokus: apakah jendela slide show bisa dibuka tanpa mengambil fokus dan tombol clicker tidak membuat slide maju dua kali | Frame berisi animasi dan video; ukuran jendela sesuai target pada DPI 100/150%; satu klik clicker = satu slide |
| P1 Audio | Tangkap suara `POWERPNT.EXE` lewat process loopback | Suara dari video di slide terdengar di NDI dan perangkat audio pilihan |
| P2 Sumber PPTX | Jenis tab baru, `PowerPointSource` (COM) dan capture jendela, masuk ke `FrameHub` | Tab PPTX tampil di layar penuh, NDI, dan OMT |
| P3 Kontrol dan UI | Tombol next/prev/goto, status slide, strip thumbnail slide di bawah pratinjau, dialog pilih file, pesan jelas kalau PowerPoint tidak ada | Operator bisa menjalankan seluruh deck tanpa menyentuh jendela PowerPoint |
| P2b Alpha | Opsi "Kirim alpha" NDI dan OMT, kanvas fit dengan bar transparan | Bar tembus di vMix/Resolume, tanpa halo; opsi mati menghasilkan keluaran sama seperti sekarang |
| P3c Preview + Take | Tab PowerPoint sebagai preview dan program, Take tanpa memulai ulang show, audio dilepas saat preview, uji dua show sekaligus | Take dari preview ke program mulus; audio PowerPoint tidak bocor ke output saat preview; perilaku dua tab PowerPoint terdokumentasi |
| P3b Clicker | Pemetaan tombol, mode fokus, mode global opsional dengan lencana, pengaturan pemetaan | Deck 12 slide dijalankan penuh dengan clicker pada mode fokus dan mode global; tidak ada loncatan dua slide |
| P4 Ketahanan | PowerPoint tertutup paksa, file hilang, dialog tak terduga (misalnya pemulihan, perlindungan file), deck panjang, soak | Aplikasi pulih atau menampilkan error yang jelas, tidak membeku |
| P5 Opsional | "Siapkan sebagai MP4" untuk PC tanpa PowerPoint (opsi C) | Hasil ekspor diputar sebagai tab video |

Perubahan versi dan `CHANGELOG.md` mengikuti kebiasaan proyek. Memakai nomor khusus seperti `1.1.6-custom` agar tidak tertukar dengan rilis resmi.

## Risiko dan hal yang belum diketahui

- PowerPoint wajib terpasang dan berlisensi. Office versi Microsoft Store/Click-to-Run kadang berperilaku beda saat dikontrol lewat COM.
- Jendela slide show di monitor dengan DPI berbeda bisa memberi ukuran piksel tidak sesuai harapan.
- Transisi dan video kadang dipercepat perangkat keras (GPU) dan bisa tampil hitam saat ditangkap. Harus diuji, belum ada bukti.
- Mulai show pertama bisa lambat (puluhan detik untuk deck besar dengan banyak video). Perlu tombol "siapkan" sebelum acara.
- PowerPoint bisa memunculkan dialog yang memblokir COM. Harus ada batas waktu dan pesan jelas.
- Media yang ditautkan (bukan ditanam) di PPTX bisa hilang di PC lain. Beri peringatan "kemas media" pada operator.
- Kebijakan perusahaan atau antivirus bisa memblokir automation Office.

- Clicker yang memakai perangkat lunak atau protokol sendiri (bukan keyboard HID) tidak akan terbaca. Merek dan model clicker yang dipakai belum diketahui.
- Mode global bisa mengganggu aplikasi lain di PC yang sama, jadi harus opsional dan terlihat jelas saat aktif.

## Keputusan yang dibutuhkan dari Anda

1. ~~PowerPoint terpasang~~ Dijawab: pasti terpasang di PC acara.
2. ~~Preview + Take~~ Diputuskan: tab PowerPoint boleh jadi preview dan program.
3. ~~Resolusi~~ Diputuskan: scale to fit, file PPTX tidak diubah.
4. ~~Alpha~~ Diputuskan: Blackout = hitam opaque; bar scale to fit = transparan.
5. ~~Clicker~~ Dijawab: model yang umum dipakai; pemetaan bawaan di atas. Masih terbuka: apakah mode fokus cukup atau mode global juga perlu.
6. Konfirmasi: perintah slide (next/prev/loncat/clicker) boleh dipakai pada tab PowerPoint yang sedang program di mode Preview + Take?
