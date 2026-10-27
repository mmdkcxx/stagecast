# Rencana per fase: sumber PDF dan lapisan sumber dokumen

Status: rencana, belum dikerjakan. Hanya di branch `custom`; tidak diubah atau digabung ke `main` sampai pemilik meminta. Belum ada kode yang diubah dan belum diuji. Dokumen induk: `docs/PLAN_PDF.md`. Lapisan yang dibangun di sini juga dipakai `docs/PLAN_PPTX.md`.

Aturan proyek yang berlaku di semua fase: typecheck selama membangun (`scripts/check.sh typecheck`), batch pengujian penuh (`check.sh all`) hanya saat pemilik meminta, kode baru dilaporkan "typechecked, belum diuji" sampai dijalankan, tidak ada fase ditandai lulus sebelum pengujiannya berjalan, output (layar penuh, NDI, OMT) tetap dinyalakan manual, komentar kode dan nama mengikuti gaya kode sekitarnya.

## Gambaran urutan

D0 spike  →  D1a jenis tab dan sumber non-web  →  D1b PdfSource + FitComposer  →  D2 UI dan kontrol  →  D3 clicker dan Preview + Take  →  D4 ketahanan  →  D5 pengujian akhir dan rilis

- D0 menentukan apakah opsi A (Windows.Data.Pdf) cukup. Hasilnya bisa mengubah D1b, tetapi tidak D1a.
- D1a tidak bergantung pada hasil D0 dan bisa mulai bersamaan.
- Setelah D1b lulus uji, `PowerPointSource` (rencana PPTX) tinggal memakai D1a dan komponen bersama.

## D0 - Spike PDF

**Tujuan:** membuktikan kualitas dan kecepatan render sebelum membangun apa pun yang bergantung padanya. Dibandingkan: A (`Windows.Data.Pdf`) dan D (pdf.js di WebView2). PDFium hanya dicoba kalau keduanya kurang.

**Dikerjakan**
- Skrip uji terpisah di folder alat (bukan bagian aplikasi), memuat PDF contoh dan merender halaman ke bitmap pada 2520×1080, 1920×1080, dan 3840×2160 (scale to fit).
- Ukur: waktu render per halaman (rata-rata dan terburuk), pemakaian memori untuk 100+ halaman, dan waktu buka file.
- Uji dokumen: PDF teks biasa, PDF dari ekspor PowerPoint (16:9 dan 4:3), PDF dengan gambar besar, halaman berotasi, ukuran halaman campuran (potret dan lanskap), PDF berkata sandi, PDF besar (100+ halaman), dan satu dua PDF rumit (font tertanam langka, transparansi, lapisan).
- Bandingkan hasil dengan penampil Edge untuk file rumit (ketajaman teks, warna, tepi).
- Opsi D: halaman penampil minimal dengan pdf.js yang ditangkap lewat jalur tab web; catat kecepatan dan apakah alpha di bar bisa dipertahankan.

**Bahan dari pemilik:** 3 sampai 5 PDF yang mewakili pemakaian sungguhan (termasuk satu hasil ekspor PowerPoint dan satu yang berat). Bila belum ada, saya buat PDF uji sendiri, tetapi itu tidak mewakili file acara.

**Hasil yang dicatat:** tabel angka dan keputusan A, D, atau PDFium di `docs/SPIKE_REPORT.md` atau berkas laporan spike yang sudah ada.

**Lulus kalau:** waktu render per halaman pada 2520×1080 cukup rendah untuk pindah halaman terasa instan dengan cache; teks terbaca tajam; semua jenis dokumen di atas terbuka atau gagal dengan pesan yang jelas; keputusan pilihan teknologi tertulis.

## D1a - Jenis tab dan sumber non-web di mesin

**Tujuan:** daftar tab dan mesin render bisa memiliki tab yang bukan halaman web, tanpa mengubah perilaku tab web yang ada.

**Dikerjakan**
- Model: tambah jenis tab (`Web`, `Pdf`, kelak `PowerPoint`, `Video`) pada `TabInfo` dan `TabEntry`, dan satu bidang sumber untuk path berkas (terpisah dari `Url`). Konfigurasi lama tanpa jenis dibaca sebagai `Web` (kompatibel mundur). `TabRules.StorableUrl` hanya berlaku untuk tab web.
- Mesin: antarmuka sumber tab (mis. `ITabSource`) yang dipenuhi `PageTab` (web) dan sumber dokumen. Pemilihan tab, menjadikan program, Preview + Take, thumbnail, dan menutup tab bekerja lewat antarmuka itu. `FrameHub.Publish` dipakai sumber dokumen untuk mengirim bingkai.
- Daftar tab: nama dan ikon jenis, dialog "Tab baru" menawarkan jenis (Web aktif; PDF muncul setelah D2).
- Pilihan sumber saat tab menjadi program: bingkai berasal dari sumber program saja, dan sumber lain berhenti menerbitkan.
- Dokumen: catat kebijakan batas jumlah tab (`MaxTabs = 12`) berlaku untuk semua jenis.

**Pengujian:** unit test model dan konfigurasi (pembacaan konfigurasi lama, penyimpanan jenis dan path); self test A3 diperbarui bila perlu; tab web berperilaku sama seperti sebelumnya (batch penuh dijalankan saat pemilik meminta).

**Lulus kalau:** semua uji lama tetap lulus; konfigurasi lama terbaca; sumber tiruan (uji) bisa dijadikan program dan mengirim bingkai ke layar penuh, NDI, dan OMT.

## D1b - PdfSource dan FitComposer

**Tujuan:** satu PDF tampil di semua output dengan scale to fit.

**Dikerjakan**
- `FitComposer` di `Stagecast.Core/Frames`: menempatkan bitmap sumber di kanvas render dengan rasio terjaga. Bar hitam opaque; bar transparan saat opsi alpha menyala (mengikuti `docs/PLAN_PPTX.md` fase P2b). Murni dan bisa diuji unit.
- `PdfSource` di proyek Rendering: membuka salinan sementara, mengambil jumlah halaman dan ukuran tiap halaman, merender halaman pada ukuran akhir area fit, cache halaman saat ini dan tetangganya dengan batas memori, menerbitkan bingkai pada FPS render (bingkai diulang saat halaman tidak berubah, seperti halaman web yang diam).
- Penanganan rotasi, ukuran halaman campuran, dan kegagalan buka (pesan jelas, tidak membuat aplikasi berhenti).
- Kata sandi: antarmuka sumber menerima kata sandi dari UI, dipakai sekali untuk membuka, tidak disimpan di mana pun.

**Pengujian:** unit test `FitComposer` (4:3 dan 16:9 pada 2520×1080, 1920×1080, 3840×2160: ukuran area, posisi tengah, bar), unit test urutan halaman dan cache. Pengujian visual pada layar penuh, NDI, OMT oleh pemilik.

**Lulus kalau:** angka `FitComposer` sesuai contoh di `docs/PLAN_PPTX.md` (16:9 di 2520×1080 menjadi 1920×1080 dengan bar 300 px; 4:3 menjadi 1440×1080 dengan bar 540 px); halaman tampil tajam; pindah halaman cepat; tidak ada kebocoran memori terlihat pada deretan pindah halaman berulang.

## D2 - Antarmuka dan kontrol

**Dikerjakan**
- Jenis PDF di dialog "Tab baru", dialog pilih berkas (hanya `.pdf`), isian kata sandi di dalam aplikasi.
- Kontrol: berikutnya, sebelumnya, awal, akhir, loncat; **kotak ketik nomor halaman** (keputusan pemilik) dengan batas 1 sampai jumlah halaman dan penanganan masukan tidak valid; status "Halaman 3 / 40".
- Strip thumbnail di bawah pratinjau (satu komponen bersama dengan PPTX): halaman sekarang berbingkai merah, berikutnya biru; thumbnail dibuat sesuai kebutuhan di latar belakang, halaman yang terlihat didahulukan; klik untuk loncat.
- Tombol Muat ulang: buka salinan baru dari berkas asli dan kembali ke nomor halaman yang sama; konfirmasi di dalam aplikasi bila tab sedang program.
- Thumbnail tab di daftar tab memakai halaman sekarang.
- Keadaan kesalahan: berkas hilang, PDF rusak, kata sandi salah, tampil sebagai pesan di tab dengan tombol Pilih ulang.
- Posisi UI mengikuti keadaan antarmuka saat dikerjakan: kalau top bar belum selesai, kontrol ditaruh di panel tengah yang sekarang.

**Pengujian:** unit test logika kontrol (batas, loncat, masukan tidak valid), self test untuk alur dasar dengan sumber tiruan, cek visual teks terpotong (lebar minimum 1220 px, DPI 125/150%).

**Lulus kalau:** operator menjalankan PDF dari awal sampai akhir hanya dengan antarmuka Stagecast; masukan nomor halaman yang tidak valid tidak membuat apa pun rusak.

## D3 - Clicker dan Preview + Take

**Dikerjakan**
- Penangkap tombol clicker sebagai perintah (bukan penerusan mentah): pemetaan bawaan dari `docs/PLAN_PPTX.md` (Page Down/panah kanan/spasi/Enter = berikutnya, Page Up/panah kiri/Backspace = sebelumnya, B atau titik = Blackout, F5 = awal, Esc tidak dipakai). Mode fokus bawaan; mode global opsional dengan lencana, mengikuti rencana PPTX.
- Tab PDF sebagai preview maupun program; Take tanpa memulai ulang; perintah halaman tetap bekerja pada tab PDF yang sedang program (pengecualian yang sama dengan rencana PPTX, menunggu konfirmasi pemilik).
- Target clicker: tab dokumen yang program lebih dulu; kalau tidak ada, yang di preview.

**Lulus kalau:** satu klik clicker = satu halaman; Take dari preview mulus dan tanpa suara atau kilatan aneh; mode global tidak merusak aplikasi lain saat mati.

## D4 - Ketahanan

- PDF besar (ratusan halaman) dan halaman raksasa: batas ukuran render dan memori stabil.
- Pindah halaman berturut-turut cepat, tab ditutup saat render berjalan, muat ulang saat sedang program.
- PDF rusak sebagian dan berkas yang hilang saat tab sudah terbuka.
- Soak singkat: ratusan pindah halaman dengan output menyala; periksa memori dan FPS.

**Lulus kalau:** aplikasi tidak membeku atau bocor memori, dan kegagalan menampilkan pesan yang jelas.

## D5 - Pengujian akhir dan rilis di branch

- `scripts/check.sh all` dijalankan saat pemilik meminta; hasil dilaporkan apa adanya.
- Pengujian manual pemilik: layar penuh dan videotron (bar dan ketajaman), NDI dan OMT di vMix/Resolume, clicker sungguhan.
- Naikkan versi dengan nomor khusus (mis. `1.2.0-custom.1`), tulis `CHANGELOG.md`, bangun installer uji. Tidak ada tag, rilis, atau penggabungan ke `main` tanpa permintaan pemilik.

## Bahan dan keputusan yang dibutuhkan

| Fase | Dari pemilik |
|---|---|
| D0 | 3 sampai 5 PDF contoh yang mewakili pemakaian sungguhan (satu hasil ekspor PowerPoint, satu yang berat); keputusan A/D/PDFium setelah angka keluar |
| D1a | Tidak ada; ada satu pertanyaan terbuka: apakah batas 12 tab perlu dinaikkan setelah ada jenis tab baru |
| D2 | Tinjau mockup antarmuka PDF (strip, kotak nomor halaman) sebelum dibangun bila diinginkan |
| D3 | Konfirmasi: perintah halaman boleh dipakai pada tab dokumen yang sedang program di Preview + Take |
| D5 | Pengujian manual di perangkat keras sungguhan |

## Hubungan dengan rencana lain
- `docs/PLAN_PPTX.md`: memakai D1a, `FitComposer`, strip, dan clicker; spike P0 PowerPoint bisa berjalan paralel dengan D0 dan D1a.
- `docs/PLAN_EMPTY_STAGE.md`: sumber per unit di `FrameHub` (E3) dan sumber dokumen sama-sama menambah jenis sumber bingkai; keduanya perlu dikerjakan tanpa saling menimpa, dan keadaan "tidak ada tab live" ditangani di mesin yang sama dengan D1a.
- `docs/PLAN_TOPBAR_UI.md`: posisi kontrol dokumen menyesuaikan.
- `docs/PLAN_SAFETY_GUARDS.md`: independen.
