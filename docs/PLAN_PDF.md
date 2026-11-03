# Rencana: sumber PDF di Stagecast

Status: rencana, belum dikerjakan. Dikerjakan hanya di branch `custom`; tidak diubah atau digabung ke `main` sampai pemilik meminta. Belum ada kode yang diubah dan belum diuji. Klaim tentang kemampuan komponen Windows di bawah berasal dari pengetahuan umum saya dan harus dibuktikan di spike D0.

## Permintaan pemilik (2026-10-27)
Aplikasi bisa membaca file PDF, mirip dengan PPTX: halaman ditayangkan ke layar penuh, NDI, dan OMT, dengan kontrol halaman.

## Pendekatan

### Opsi yang dipertimbangkan
| Opsi | Cara | Kelebihan | Kekurangan |
|---|---|---|---|
| **A. Windows.Data.Pdf (disarankan)** | Komponen PDF bawaan Windows 10/11 merender tiap halaman menjadi gambar pada ukuran yang diminta | Tanpa pemasangan tambahan dan tanpa lisensi; kontrol penuh atas urutan halaman; thumbnail mudah; gambar langsung masuk ke jalur bingkai | Tidak menjalankan skrip, formulir, atau media yang tertanam di PDF; fidelitas untuk PDF yang rumit harus diuji |
| B. Penampil PDF bawaan WebView2 | Buka file di tab web biasa | Sudah bisa hari ini, tanpa kode baru | Ada antarmuka penampil di gambar; navigasi halaman sulit dikontrol dari luar; tidak bisa strip thumbnail dan scale to fit yang sama |
| D. PDF.js di dalam halaman WebView2 | Halaman penampil milik Stagecast (pdf.js terkemas) di tab web, dikontrol lewat skrip | Tab PDF otomatis memakai seluruh mesin tab web yang ada (capture, thumbnail, Preview + Take) tanpa mengubah mesin | Pustaka JavaScript ikut dikemas (lisensi Apache-2.0, perlu dicatat); alpha pada bar belum tentu bisa dipertahankan lewat capture web; pengurai PDF berjalan sebagai JavaScript |
| C. Pustaka pihak ketiga (mis. PDFium) | DLL asli ikut dikemas | Fidelitas tinggi, jalan tanpa komponen Windows | Menambah DLL dan lisensi untuk dikemas; belum perlu kecuali D0 menunjukkan A kurang |

Rekomendasi: **A**, dengan **D** sebagai pembanding di spike D0. Tab web ke file PDF tetap bisa dipakai seperti sekarang; tab PDF baru memberi pengalaman setara PPTX.

### Temuan penting dari kode (mempengaruhi rancangan)
- Tab sekarang selalu halaman web: `TabInfo(Id, Name, Url, ProfileId)` di `Stagecast.Core/Tabs/TabList.cs`, `PageTab` dengan WebView2 di mesin render, dan `TabEntry.Url` di konfigurasi (`AppConfig.cs`). Batas tab `TabRules.MaxTabs = 12`.
- `FrameHub.Publish(Frame)` tidak peduli siapa pengirimnya, jadi sumber baru bisa mengirim bingkai langsung. Yang belum ada adalah tab yang bukan WebView2 di `RenderEngine`: memilih, menjadikan program, thumbnail, menutup, dan Preview + Take saat ini mengandaikan `PageTab`.
- Jadi pekerjaan terbesar bukan merender PDF, tetapi memberi mesin dan daftar tab konsep "jenis tab" (web, PDF, PowerPoint) dan "sumber non-web". Itu juga yang dibutuhkan sumber PowerPoint, jadi dikerjakan sekali di fase D1a.

### Beda dengan PPTX
- Tidak butuh PowerPoint dan tidak ada capture jendela. Stagecast merender sendiri halaman PDF menjadi gambar, lalu menempatkannya di kanvas dengan scale to fit. Karena itu bagian ini lebih sederhana dan lebih mudah diuji.
- PDF tidak punya animasi teks atau video. Perpindahan halaman berupa potongan langsung (tanpa transisi).
- Ada pertimbangan khusus PDF: ukuran halaman bisa berbeda-beda dalam satu file, ada rotasi halaman, dan file bisa berkata sandi.

## Komponen bersama dengan PPTX
Agar dua jenis sumber ini tidak ditulis ganda, rencana ini mengusulkan satu lapisan "sumber dokumen" yang dipakai bersama:
- **Antarmuka sumber dokumen:** jumlah halaman/slide, posisi saat ini, berikutnya, sebelumnya, loncat ke nomor, thumbnail, muat ulang. `PowerPointSource` dan `PdfSource` sama-sama mengimplementasikannya.
- **Penyusun kanvas scale to fit (`FitComposer`):** menempatkan gambar sumber di kanvas render dengan rasio terjaga. Bar sisa hitam di layar penuh dan transparan di NDI/OMT saat opsi alpha menyala (mengikuti `docs/PLAN_PPTX.md`).
- **Strip thumbnail** di bawah pratinjau, **kontrol berikutnya/sebelumnya/loncat**, **clicker** dengan pemetaan yang sama, **Preview + Take**, dan **tombol Muat ulang**. Semuanya sudah direncanakan untuk PPTX dan dipakai ulang.
- Pemilihan jenis di dialog "Tab baru": Web, Video MP4, PowerPoint, PDF.

## Perilaku PDF yang diusulkan
- **Jenis file:** hanya `.pdf` di versi pertama.
- **Salinan sementara:** file dibuka dari salinan di folder sementara Stagecast, sama seperti PPTX, supaya file asli tidak terkunci dan perubahan menit terakhir tidak mengganggu sampai operator menekan Muat ulang. Salinan dihapus saat tab ditutup.
- **Scale to fit:** tiap halaman diperbesar sampai salah satu sisinya mentok di kanvas, rasio dijaga, sisanya hitam (transparan saat alpha). File PPTX tidak diubah; PDF juga tidak diubah. Halaman dengan ukuran berbeda ditangani halaman demi halaman.
- **Rotasi:** mengikuti rotasi yang tertulis di PDF.
- **Ketajaman:** halaman dirender langsung pada ukuran akhir di kanvas (bukan diperbesar dari gambar kecil).
- **Cache:** halaman sekarang, satu sebelum, dan beberapa sesudahnya dirender di depan supaya pindah halaman terasa instan. Jumlah halaman yang ditahan dibatasi supaya memori aman untuk PDF ratusan halaman.
- **Thumbnail:** dibuat saat persiapan atau sesuai kebutuhan untuk PDF panjang (berurutan di latar belakang, halaman yang terlihat di strip didahulukan).
- **Kata sandi:** kalau PDF terkunci, tampilkan isian kata sandi di dalam aplikasi setiap kali dibuka. Kata sandi tidak disimpan di konfigurasi, log, atau di mana pun (aturan proyek). Ini asumsi saya.
- **Tautan, formulir, dan media di dalam PDF:** diabaikan. Tidak ada interaksi dengan isi halaman.
- **Transisi:** tidak ada (potongan langsung). Opsi lintas pudar tidak direncanakan.
- **Audio:** senyap. Fitur burst atau nada uji tidak terkait.
- **File tidak tepercaya:** PDF yang diunduh diperlakukan sebagai input tidak tepercaya. Penguraiannya dilakukan komponen Windows di dalam proses aplikasi; jaga Windows tetap diperbarui. Kegagalan membuka file ditampilkan sebagai pesan yang jelas di tab, bukan kerusakan aplikasi.
- **Konfigurasi:** hanya path file, nomor halaman terakhir, dan opsi tersimpan; isi PDF dan kata sandi tidak.

## Rencana eksekusi (nanti)

Aturan proyek: typecheck selama membangun, batch pengujian penuh hanya saat diminta, tidak ada fase ditandai lulus sebelum diuji, output tetap dinyalakan manual.

| Fase | Isi | Cara menyatakan lulus |
|---|---|---|
| D0 Spike | Skrip kecil memakai `Windows.Data.Pdf`: buka PDF, render halaman ke ukuran 2520×1080 dan 3840×2160, ukur waktu per halaman, pakai memori, uji PDF berkata sandi, halaman berotasi, ukuran halaman campuran, PDF besar (100+ halaman), dan PDF dari ekspor PowerPoint. Bandingkan hasil dengan penampil Edge untuk satu dua file rumit | Waktu render per halaman tercatat; kualitas teks dan gambar dapat diterima; keputusan tetap di opsi A atau pindah ke C |
| D1 Lapisan sumber dokumen | Antarmuka sumber dokumen bersama, `FitComposer`, `PdfSource` yang memasukkan bingkai ke `FrameHub` | Satu PDF tampil di layar penuh, NDI, dan OMT dengan scale to fit; bar transparan saat alpha menyala (setelah P2b) |
| D2 Kontrol dan UI | Jenis tab PDF di dialog Tab baru, berikutnya/sebelumnya/loncat, status "Halaman 3 / 40", strip thumbnail, Muat ulang, isian kata sandi, pesan kesalahan | Operator menjalankan satu PDF penuh tanpa menyentuh berkas lain |
| D3 Clicker dan Preview + Take | Pemetaan clicker yang sama dengan PPTX; PDF sebagai preview maupun program | Satu klik clicker = satu halaman; Take dari preview mulus |
| D4 Ketahanan | File hilang, PDF rusak, PDF sangat besar, perpindahan cepat berturut-turut, memori jangka panjang | Pulih atau pesan jelas, tidak membeku, memori stabil |

## Urutan terhadap PPTX
Saran saya: kerjakan lapisan bersama (D1 dan D2) bersama PDF lebih dulu, karena PDF tidak punya risiko COM, jendela, atau audio. Setelah itu `PowerPointSource` tinggal memakai antarmuka dan strip yang sudah ada. Spike P0 PowerPoint tetap bisa jalan paralel, karena hasilnya yang menentukan apakah jalur PPTX layak. Urutan akhir ditentukan pemilik.

## Risiko dan hal yang belum diketahui
- Fidelitas `Windows.Data.Pdf` untuk PDF rumit (font tertanam langka, transparansi, lapisan, pola) belum diuji.
- Waktu render per halaman pada 3840×2160 dan PDF dengan gambar besar belum diketahui; bisa terasa lambat tanpa cache.
- PDF dengan halaman raksasa (peta, poster) bisa memakan memori besar; perlu batas ukuran render.
- PDF yang berasal dari ekspor PowerPoint kehilangan animasi dan video; operator harus tahu bahwa itu bukan pengganti file PPTX untuk deck beranimasi.
- Ruang lingkup tidak mencakup: anotasi, pencarian teks, zoom manual, mode dua halaman, dan bookmark.

## Keputusan pemilik (2026-10-27)
1. **Urutan:** PDF dikerjakan dulu, bersama lapisan bersama (jenis tab, sumber dokumen, `FitComposer`, strip, clicker). Spike PowerPoint (P0) mengikuti setelahnya atau paralel bila pemilik meminta.
2. **Kata sandi PDF:** isian setiap kali dibuka, tidak pernah disimpan.
3. **Loncat halaman:** ada kotak kecil untuk mengetik nomor halaman, selain klik thumbnail dan tombol berikutnya/sebelumnya.

Rencana per fase yang terperinci ada di `docs/PLAN_PDF_PHASES.md`.
