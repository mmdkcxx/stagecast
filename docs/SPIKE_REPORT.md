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
