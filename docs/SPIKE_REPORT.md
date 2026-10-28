# Laporan GATE: hasil spike S0-S12

Tanggal: 2026-10-27. Mesin uji: laptop Windows 11 (build 26300), AMD iGPU (menggerakkan layar 1920x1080 144 Hz) dan NVIDIA GTX 1650, WebView2 Runtime 155. Semua angka dari satu mesin; rincian dan cara uji ada di `PLAN_PHASES.md`.

## Ringkasan

Jalur WebView2 (composition hosting) + Windows.Graphics.Capture **lulus** untuk semua risiko yang diuji. Jalur cadangan CEF tidak diperlukan. Ada beberapa batasan yang harus dibawa ke desain (di bawah).

| Tahap | Pertanyaan | Hasil |
|---|---|---|
| S3 | Viewport halaman mengikuti resolusi pilihan, bebas dari ukuran jendela/monitor? | Ya, 16/16 kombinasi (4 resolusi x 4 skala), dengan skala disimulasikan lewat RasterizationScale. Skala Windows asli belum diuji. |
| S4 | Visual di luar layar dan lebih lebar dari layar bisa di-capture? | Ya, 2520x1080 tepat, isi benar. |
| S5 | 30/60 fps stabil? | 60,0 fps selama 5 menit, CPU 0,3%. Rata-rata tepat; kerataan tidak sempurna (2,3% interval >25 ms). |
| S6 | Konten nyata: H.264, login? | MP4 H.264 jalan, 0 frame dibuang. Login Canva lewat Google berhasil di browser tertanam; sesi dilaporkan aman oleh operator. |
| S7 | GPU mana? | iGPU lebih rata; GTX memaksa salinan lintas-GPU. Operator mengatur lewat Windows Graphics Settings. |
| S8 | Audio proses bisa ditangkap, mute berfungsi? | Ya, dengan target proses browser WebView2 (target proses host senyap). `IsMuted` membuat senyap total. |
| S9 | Dua tab, pindah sumber? | Ya, gambar berganti 32-44 ms, audio eksklusif per tab. Menyembunyikan tab non-aktif memangkas CPU-nya dari ~4% ke ~0,1-1,6%. |
| S10 | Halaman bisa dioperasikan dari preview? | Ya: klik akurat (selisih maks 1 px), scroll, keyboard, zoom halaman, kursor, latensi klik-ke-frame median 45 ms. |
| S11 | Skala anamorfik tetap tajam? | Teks 14 px ke atas terbaca di semua filter yang benar; bicubic dipilih sementara; detail 1 px memudar lewat HDMI (batas fisik). Uji LED belum. |
| S12 | Preview dan program bersamaan? | 60 fps program bertahan, audio tidak bocor, Take 6-26 ms tanpa kedip. Kerataan turun (~10% interval >25 ms). |

## Batasan yang dibawa ke desain

1. **Pacing output harus berbasis timer.** Frame dari capture datang tidak merata (88-143/dtk, turun ke 65-98/dtk dengan dua tab). Setiap output mengirim pada detak tetap 30/60 Hz dan mengulang frame terbaru; jangan meneruskan frame mentah.
2. **`MinUpdateInterval` wajib diatur** (tanpa itu capture hanya ~48 fps). Butuh proyeksi SDK 10.0.26100 dan Windows 11; fallback Windows lama belum diuji.
3. **Audio per proses browser, bukan per proses aplikasi.** Target loopback = `CoreWebView2.BrowserProcessId`. Mute per tab lewat `IsMuted` cukup untuk mencegah kebocoran, juga bila tab berbagi profil.
4. **Autoplay:** perlu argumen `--autoplay-policy=no-user-gesture-required` agar video/suara tanpa klik berjalan.
5. **Popup login dibuka di tab yang sama** (cukup untuk Canva + Google). Alur OAuth lain belum diuji.
6. **Zoom halaman mengubah isi output** (viewport CSS berubah), berbeda dari zoom tampilan preview.
7. **Tab non-aktif disembunyikan secara bawaan** (hemat CPU) kecuali tab itu Preview; animasi/kuis di tab tersembunyi mungkin tidak ter-update [U].
8. **Detail 1 px tidak bertahan lewat HDMI** (1920 piksel membawa 2520 piksel). NDI/OMT mengirim 2520 asli.

## Belum terbukti (jangan dianggap aman)

- Skala Windows asli (125/150%) untuk jendela aplikasi; hanya disimulasikan.
- Halaman berat (Canva dengan animasi) saat dua tab aktif; dua video H.264 sekaligus.
- NDI, OMT, dan layar penuh anamorfik di monitor eksternal (belum dibangun; hanya satu layar tersedia saat spike).
- Kesan visual kelancaran dan ketajaman di LED nyata [Anda]; audio asli dari video/Kahoot [Anda].
- Beban total saat semua output berjalan bersamaan.
- Pemulihan bila proses WebView2 crash; perilaku saat perangkat audio/monitor dicabut.

## Rekomendasi

**Lulus dengan batasan: lanjut ke Bagian 2 (A1 dan seterusnya).** Tidak ada hasil yang mengharuskan pindah ke CEF. Keputusan ini milik Anda.

## D0 - Spike PDF dengan Windows.Data.Pdf (2026-10-27)

Alat: `spikes/Stagecast.PdfSpike` (di luar solusi aplikasi). Dijalankan sekali pada 3 PDF milik pemilik (3 file ekspor presentasi, semua 16:9, tanpa kata sandi, tanpa rotasi). Mesin: PC pengembangan pemilik, build Release. Halaman dirender scale to fit ke kanvas (hasil gambar 2520×1080 → 1920×1080, bar ditambahkan nanti oleh `FitComposer`).

| File | Halaman | Ukuran | Buka | 2520×1080 BMP rata-rata / terburuk | 3840×2160 BMP rata-rata / terburuk |
|---|---|---|---|---|---|
| Keynote Gubernur DKI | 18 | 7,2 MB, 1280×720 pt | 563 ms (pertama, termasuk pemanasan) | 48 / 79 ms | 77 / 125 ms |
| Paparan Bu Shanty (berat, kolase foto) | 13 | 30,3 MB, 1920×1080 pt | 14 ms | 74 / 97 ms | 112 / 212 ms |
| Opening Remarks Prof Firdaus Ali | 5 | 11,6 MB, 1920×1080 pt | 14 ms | 71 / 103 ms | 116 / 165 ms |

- Keluaran PNG (bawaan API) jauh lebih lambat: 140–175 ms pada 2520×1080 dan 320–400 ms pada 3840×2160. Pakai keluaran BMP/bitmap mentah, bukan PNG.
- Merender semua halaman berurutan pada 2520×1080: 44–72 ms per halaman. Working set proses naik dari 36 MB ke 161–228 MB setelah GC dan turun lagi ke 190 MB, tidak naik terus di antara file (belum diuji dengan 100+ halaman).
- Pemeriksaan visual (halaman 5 Keynote dan halaman 5 Paparan): teks tajam, warna wajar, foto dan transparansi benar, font tertanam benar.
- Kebutuhan memori bingkai: 1920×1080 BGRA = 8,1 MB, 3840×2160 = 32 MB per halaman di cache.

**Keputusan sementara: opsi A (Windows.Data.Pdf) cukup**; opsi D (pdf.js) dan PDFium tidak dicoba karena A memenuhi kriteria kecepatan dan ketajaman pada file-file ini. Dengan 50–110 ms per halaman, pindah halaman terasa instan bila halaman berikutnya dirender lebih dulu di latar belakang.

**Belum dibuktikan** (file contoh tidak ada): PDF berkata sandi, halaman berotasi, ukuran halaman campuran (potret dan lanskap), PDF 100+ halaman, PDF dengan font langka atau lapisan, perbandingan dengan Edge, 4:3. Itu masuk D1b/D4 dan diuji bila ada file. Alpha pada bar transparan juga belum dibuktikan (bukan bagian spike ini).

## P0 - Spike PPTX dengan PowerPoint COM + Windows Graphics Capture (2026-10-28)

Alat: `spikes/Stagecast.PptxSpike` (di luar solusi aplikasi; mode `make`, `probe`, `grab`). Dijalankan di PC ini: Office16 (build 20527), monitor tunggal 1920×1080, DPI 100%, dengan 2 deck uji buatan sendiri (16:9 dan 4:3, 8 slide, slide 1/4/8 tersembunyi, slide 3 punya animasi fade klik + sesudah-sebelumnya). Belum diuji: video MP4, deck asli pemilik, DPI 150%, dua monitor, suara.

**Terbukti (COM, late-binding tanpa pustaka Office)**
- Buka salinan sementara read-only tanpa jendela: 70–200 ms. `AutomationSecurity = 3` bisa dipasang. Memulai PowerPoint: sekitar 0,6 s.
- `Slide.Export` PNG 320×180 berhasil untuk **semua** slide termasuk yang tersembunyi: 8 slide dalam 70–210 ms, dan juga aman dipanggil **saat show berjalan** (87 ms).
- Slide tersembunyi: show mulai di slide 2 (slide 1 tersembunyi), `Next` melewati slide 4 (3→5) dan slide 8, `First()` menuju 2, `Last()` menuju 7. `GotoSlide` **bisa** menuju slide tersembunyi (1, 4, 8). Sesuai keputusan pemilik.
- `Next` juga memajukan animasi: slide 3 (dua efek) butuh 3 kali `Next` sebelum pindah ke slide 5. Maju otomatis memang dimatikan (`AdvanceMode = 1`).
- Setelah slide tampil terakhir, `Next` pindah ke posisi `jumlah+1` (layar "akhir show"); `Next` sekali lagi **mengakhiri show dan menutup presentasi tanpa jendela**. Aplikasi harus berhenti di slide terakhir, bukan memanggil `Next` melewatinya.
- PowerPoint sering menjawab `RPC_E_SERVERCALL_RETRYLATER` (0x8001010A) saat animasi atau pindah slide: semua panggilan COM harus diulang dengan jeda (bungkus `Retry`).
- `SlideShowWindow.HWND` **ditolak** COM di sini; jendela dicari lewat proses PowerPoint dan judul "PowerPoint Slide Show".
- Windows Graphics Capture dari HWND jendela show jalan: `IsCursorCaptureEnabled = false` menghilangkan kursor, `IsBorderRequired = false` tidak error, warna slide persis (hijau 40,160,60; ungu 160,60,180), animasi menghasilkan puluhan frame (22–57) dan slide diam hanya 1 frame (seperti halaman PDF).
- Dengan show layar penuh, capture **tetap mengikuti slide dan animasi saat jendela lain (topmost, seukuran layar) menutupinya**, dan `Next` lewat COM bekerja walau show bukan jendela depan.

**Temuan yang mengubah rancangan**
1. **Mode windowed tidak bisa dipakai untuk piksel penuh.** Jendelanya berbingkai (title bar 31 px, scrollbar, tepi 16 px lebar), area isi lebih kecil dari ukuran luar yang diminta (1456×1119 diminta, 1456×1100 didapat), dan bagian jendela di luar layar tidak dirender sehingga tertangkap hitam. Pada monitor 1080 px tidak mungkin mendapat slide setinggi 1080 px tanpa bingkai.
2. **Mode layar penuh bersih dan tepat:** jendela `screenClass` 1920×1080 di (0,0), slide 4:3 tepat 1440×1080 di tengah (x 240–1680) dengan bar hitam di sisi. Kelemahan: show menutupi monitor tempat ia dimulai.
3. **Toolbar mengambang PowerPoint (panah, pena, dll.) muncul di kiri bawah frame** setelah mouse digerakkan di atas show, dan ikut tertangkap. Kursor sendiri tidak tertangkap. Cara mematikan toolbar lewat COM belum ditemukan.
4. `Quit` pada instance yang dijalankan spike tidak pernah selesai (proses harus dimatikan paksa); aplikasi nyata hanya menutup presentasi miliknya dan tidak memanggil `Quit` pada PowerPoint milik pengguna (sudah diputuskan).

**Belum dibuktikan:** video MP4 dan audio (P1), pengaturan "animasi sesudah-sebelumnya dan video otomatis tetap jalan" selain fade, DPI 150%, PowerPoint sudah terbuka dengan dokumen lain, dua slide show sekaligus, penempatan show di monitor kedua, dan cara menyembunyikan toolbar mengambang.

**Tambahan P0 (2026-10-28, deck asli pemilik dan jendela di luar layar)**
- Deck asli (`PPT Komunikasi Pemasaran Sesi 7.pptx`): 32 slide, 4:3 (1024×768 pt), tanpa slide tersembunyi. Buka 493 ms, ekspor 32 thumbnail 928 ms (832 ms saat show berjalan), `Next`/`GotoSlide`/`First`/`Last` normal (slide 4 butuh 2 kali `Next` karena animasi).
- **Show layar penuh dipindah ke luar layar (`SetWindowPos` ke x = −4000) tetap dirender dan tertangkap penuh** oleh Windows Graphics Capture: frame 1920×1080 benar dengan slide 4:3 1440×1080 di tengah, animasi fade-in terlihat di tengah gerak (203–212 frame saat navigasi). Jadi tidak perlu monitor kedua atau jendela penutup, dan operator tidak melihat jendela PowerPoint, seperti web render.
- Konsekuensi: mouse tidak pernah berada di atas jendela show, sehingga toolbar mengambang PowerPoint (temuan 3) kemungkinan besar tidak muncul. Perlu dibuktikan dengan clicker/mouse sungguhan.
- Belum diuji: jendela show sempat tampil di layar sebelum dipindah (kilatan sekitar 1 s saat mulai); apakah ada ikon di taskbar; deck dengan video MP4 dan audio; DPI 150% dan dua monitor (pindah ke x = −4000 pada konfigurasi monitor kiri-kanan bisa jatuh di monitor lain, perlu koordinat di luar semua monitor).

## P1 - Audio dan video PowerPoint (2026-10-28)

Alat: mode `audio` di `spikes/Stagecast.PptxSpike` (memakai `ProcessLoopbackCapture` milik aplikasi, ditautkan) dan self test `p1` di aplikasi. Bahan uji dibuat PowerPoint sendiri: deck dengan WAV 440 Hz, dan MP4 hasil `CreateVideo` dari dua slide (merah 2 dtk, hijau 2 dtk).

- **Loopback pada proses `POWERPNT.EXE` menangkap suara show** (peak 0,29 untuk WAV, 0,235 untuk MP4). Catatan S8 (proses aplikasi senyap) hanya berlaku untuk WebView2; PowerPoint tidak perlu proses anak.
- **Video MP4 dalam show yang dipindah ke luar layar tertangkap dan bergerak** lewat jalur GPU (143 frame/dtk selama klip; frame bukan hitam). Di aplikasi, output berganti merah lalu hijau mengikuti klip.
- **Media hanya main sendiri bila diberi efek animasi "play"** (`msoAnimEffectMediaPlay` = 83, setelah sebelumnya); `AnimationSettings.PlaySettings.PlayOnEntry` saja tidak cukup. Deck pemilik yang dibuat di PowerPoint biasanya sudah punya efek itu.
- Suara deck hanya diteruskan ke output saat tab-nya tayang (`RenderEngine.SilentAudioProcessIds` + `BrowserAudioCapture.SetSilent`); Freeze membisukannya, Blackout tidak. Terbukti di `p1`: terdengar saat tayang, senyap saat tab web tayang (setelah meter turun), terdengar lagi saat kembali.
- **Batasan:** semua deck di satu PowerPoint memakai satu proses, jadi suara tidak bisa dipisah per deck; dua tab PPT sekaligus berbagi satu aturan (terdengar bila salah satu tayang). PowerPoint milik pengguna yang dipinjam ikut tertangkap suaranya (deck lain yang terbuka).
- **Belum diperbaiki:** PowerPoint tetap memutar suara ke speaker Windows (perangkat bawaan) walau tab sedang preview atau tidak tayang; Stagecast hanya menahan salinan yang masuk ke output. Perlu keputusan: mematikan sesi suara PowerPoint di Windows saat tidak tayang (belum diuji apakah loopback tetap menerima suara).

## P3a: peringatan font dan berkas tertaut, deck asli lewat aplikasi (2026-10-28)

- Setelah deck dibuka, `PowerPointSource.CheckDeck` memeriksa font yang tidak disematkan dan tidak terpasang di PC ini (daftar WPF + nama face di registri, jadi "Calibri Light" tidak salah dianggap hilang) serta media/gambar/OLE tertaut yang berkasnya tidak ada. Hasilnya `Warnings`, ditampilkan sebagai pesan di tab ("Perhatian: ...").
- Tes `p2` memakai teks berfont palsu dan memeriksa peringatannya. Tes `p3` (tidak masuk `check.sh`, berkas milik operator, `STAGECAST_DECK` mengganti jalurnya) menjalankan deck asli 32 slide lewat aplikasi: lulus, 37 langkah Next (animasi ikut dihitung) dalam sekitar 4,5 detik, semua thumbnail terbuat, peringatan: font "Gill Sans" (font Mac) tidak ada di Windows.

## P3c: PowerPoint di Preview + Take (2026-10-28)

- Tes `p4` (masuk smoke): deck di preview berjalan dan bisa digerakkan tanpa apa pun masuk ke output, suaranya tidak diteruskan; Take memasukkan slide yang sudah disiapkan tanpa memulai ulang show (proses dan nomor slide sama) dan suaranya mulai diteruskan; clicker menggerakkan deck program. Dua deck sekaligus (satu program, satu preview) bekerja dalam satu PowerPoint, Take menukarnya.
- Bug yang ditemukan: deck pertama yang memulai PowerPoint, saat ditutup, menunggu 4 detik lalu membunuh prosesnya walau deck kedua masih berjalan. Sekarang PowerPoint yang dimulai deck kita dipakai bersama; hanya deck terakhir yang menutup yang memerintahkan Quit (dan hanya itu yang boleh membunuh bila Quit macet).
- Koreksi: bar "PROGRAM" (`ProgramDocBar`) untuk deck yang tayang saat preview-nya tab lain sudah ada sejak PDF; yang kurang: loncat ke slide tersembunyi lewat bar itu tidak meminta konfirmasi (sudah diperbaiki, diuji di `p4`). Belum ada: strip thumbnail untuk deck program saat preview-nya tab lain.

## P4: ketahanan PowerPoint (2026-10-28)

- Tes `p5` (masuk smoke; dilewati bila PowerPoint milik operator sedang jalan karena langkahnya mematikan POWERPNT): PowerPoint operator dengan dokumen belum tersimpan tidak tersentuh saat tab ditutup; PowerPoint dimatikan paksa di tengah show dilaporkan di tab dalam beberapa detik, gerak slide gagal tanpa menggantung, output tetap di gambar terakhir; Muat ulang menyalakan PowerPoint lagi dan slide tayang kembali; berkas yang hilang ditolak dan deck lama tetap jalan; 150 gerak Next/Previous berturut-turut berakhir di slide yang sama dengan hitungan dan gambar output.
- Diperbaiki: setelah PowerPoint mati, Muat ulang gagal terus karena masih memakai referensi COM yang mati (sekarang dibuang dan PowerPoint dimulai ulang); ditambah pemeriksa tiap 2 detik yang melapor "PowerPoint berhenti" tanpa menunggu gerak slide berikutnya.
- Belum diuji: dialog tak terduga (pemulihan, perlindungan file/Protected View, kata sandi), deck sangat panjang, soak berjam-jam, PowerPoint ditutup pengguna lewat jendelanya.
