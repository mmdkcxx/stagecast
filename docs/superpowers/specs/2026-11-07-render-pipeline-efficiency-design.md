# Efisiensi jalur render: desain eksperimen (branch `beta`)

Tanggal: 2026-11-07 · Status: menunggu persetujuan · Cabang: `beta` (main tidak disentuh)

## Maksud

Saat dipakai, Stagecast mengalami framedrop (paling terasa di preview, kadang juga di output HDMI), terutama ketika
membuka website, dan kipas laptop langsung naik. Tujuan eksperimen: jalur dari halaman ke preview dan output dibuat
seefisien mungkin, **berdasarkan angka hasil ukur**, bukan dugaan.

## Tujuan

1. Framedrop di preview dan HDMI turun jelas, terutama saat halaman dibuka.
2. Beban CPU dan suhu turun pada kondisi yang sama.
3. Setiap perubahan punya angka sebelum dan sesudah (satu skenario ukur yang bisa diulang).

## Bukan tujuan

- Mengganti mesin render (Servo, Ultralight, CEF). Tidak menyentuh penyebab yang ditemukan dan menurunkan kompatibilitas web.
- Menulis ulang aplikasi ke Rust.
- Mengubah perilaku yang terlihat pengguna (tampilan, fitur, format file sesi).

## Anti-tujuan (dianggap gagal walau secara teknis jalan)

- Output NDI/OMT/HDMI berubah warna, kehilangan alpha, atau tertinggal (latensi naik).
- Frame preview jadi lebih lambat dari sekarang.
- Perbaikan yang tidak bisa dibuktikan lewat pengukuran.

## Temuan yang mendasari (dari kode, belum diukur)

1. `FrameCapturer` membaca setiap frame output dari GPU ke CPU pada ukuran penuh (`CopyResource` + `Map`, sinkron).
   Pada 2520×1080 BGRA sekitar 11 MB per frame, sekitar 650 MB/s pada 60 fps.
2. Preview dan output memakai satu thread capture dan satu lock D3D (`_deviceGate`); readback preview dapat menunda frame output.
3. Preview dibuat sebagai `BitmapSource` baru tiap frame dan dikirim lewat Dispatcher; jadwalnya 66 ms (`PreviewIntervalMs`).
4. WGC dikonfigurasi menerima semua frame compositor (65–140/detik) lalu sebagian dibuang.
5. `NdiOutput.Connections` dan `OmtOutput` jumlah penerima hanya dipakai untuk teks status; readback dan pengiriman jalan walau tidak ada penerima.
6. NDI dan OMT mengirim BGRA penuh (4 byte/piksel); UYVY akan setengahnya.
7. Beban Chromium (render halaman) kemungkinan bagian terbesar dan di luar jalur frame; perlu diukur terpisah.

## Pendekatan yang dipertimbangkan

| | Isi | Catatan |
|---|---|---|
| **A (dipilih)** | Ukur dulu, lalu perbaiki jalur yang ada | Risiko rendah, dampak terbesar pada framedrop |
| B (tahap lanjut) | A + HDMI langsung dari GPU (swapchain sendiri) | Hanya jika HDMI masih drop setelah A; NDI/OMT tetap butuh readback |
| C (ditolak) | Ganti mesin render | Tidak menyentuh penyebab, risiko kompatibilitas |

Pilihan pengguna: A, B sebagai tahap berikutnya bila perlu, dimulai dari pengukuran.

## Rancangan A (per tahap, tiap tahap diukur sebelum lanjut)

**Tahap 0: Pengukuran.** Skenario tetap yang bisa diulang (buka beberapa halaman nyata pada resolusi dan fps biasa;
preview hidup; HDMI aktif; NDI/OMT dengan dan tanpa penerima). Catat: waktu readback (rata-rata, p99, maks), frame
terlewat di preview dan di output, jumlah stall, CPU dan GPU per proses (Stagecast vs `msedgewebview2`), suhu atau
frekuensi bila tersedia. Dibangun di atas `CaptureStats`, statistik hub, dan `scripts/soak.sh`. Hasil disimpan sebagai
baseline di `docs/`.

**Tahap 1: Lewati pekerjaan yang tidak perlu.**
- NDI/OMT tanpa penerima: lewati readback dan pengiriman (pakai `Connections`).
- Halaman diam: kirim ulang buffer terakhir tanpa readback baru.
- Tab yang tidak tampil di panggung atau preview: hentikan capture-nya bila memungkinkan.

**Tahap 2: Hilangkan stall.**
- Readback asinkron dengan buffer staging ganda (frame N dibaca saat GPU mengerjakan N+1; terima tambahan 1 frame latensi hanya bila terukur dapat diterima).
- Pisahkan readback preview dari thread dan lock output.

**Tahap 3: Perkecil data.**
- Konversi ke UYVY di GPU sebelum readback bila alpha tidak dikirim (`SendAlpha` mati). BGRA tetap dipakai bila alpha diperlukan.

**Tahap 4: Preview.**
- Tampilkan preview langsung dari texture GPU (mis. `D3DImage`) tanpa `BitmapSource` per frame; evaluasi kenaikan fps preview.

**Tahap 5: Beban saat memuat halaman.**
- Uji setelan WebView2 (prioritas proses, throttling latar, flag GPU) satu per satu dengan skenario ukur yang sama.

**Tahap B (hanya bila perlu):** output HDMI dari swapchain D3D langsung tanpa lewat CPU.

## Batasan

- Hanya Windows; D3D11, Windows Graphics Capture, WebView2 tetap.
- Perilaku visual output harus identik (diverifikasi lewat self test yang ada dan perbandingan piksel bila tersedia).
- Pengujian penuh dijalankan per tahap sebagai batch (`check.sh all`), typecheck selama membangun.
- Pekerjaan di `beta`; tidak ada rilis; `main` tidak disentuh sampai diminta.

## Diserahkan kepada pelaksana

- Bentuk tepat skenario ukur, nama metrik, dan format laporan baseline.
- Urutan di dalam tahap 1 dan pilihan teknis (fence vs polling untuk readback asinkron).
- Apakah tahap 5 menghasilkan perubahan atau hanya catatan temuan.

## Angka yang diharapkan (perkiraan kasar, belum diukur, akan diganti hasil tahap 0)

Framedrop turun sekitar 50–80% dengan A; beban CPU/panas turun sekitar 10–25%. Dengan B (HDMI saja) panas bisa turun
sekitar 25–40% dan framedrop sekitar 70–90%. Bagian terbesar beban kemungkinan Chromium dan tidak tercakup.
