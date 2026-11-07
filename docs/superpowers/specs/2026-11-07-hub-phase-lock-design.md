# Mengunci fase tick hub ke jadwal capture: desain singkat

Tanggal: 2026-11-07 · Cabang: `beta` · Bagian dari `2026-11-07-render-pipeline-efficiency-design.md` (tambahan setelah tahap 1)

## Masalah

`FramePump` memanggil `FrameHub.Tick` pada jam 60 Hz-nya sendiri. `FrameCapturer` menerbitkan satu frame per interval pada jadwal lain
(dipilih dari frame compositor yang terdekat dengan jadwal ideal, lalu terbit setelah salinan GPU selesai, 4–10 ms kemudian).
Kedua jam punya periode sama, jadi selisih fasenya tetap sepanjang run dan ditentukan oleh saat mulai. Bila frame terbit tepat di
sekitar batas tick, jitter beberapa ms membuat satu tick tanpa frame baru (mengulang) dan tick berikutnya dua frame (satu dibuang).
Terlihat: `ulang = dibuang` berkisar 3–119 dari 480 tick antar-run (komentar tes `a2`), sekitar 15% pada fase buruk.

## Pendekatan yang dipilih: PLL ringan pada waktu tick

Hub mengukur untuk setiap frame yang terbit: berapa ms setelah tick terakhir ia tiba (`sinceTick`). `PhaseLock` (Core, murni) menjaga
rata-rata melingkar dari 24 pengukuran terakhir dan menghitung koreksi kecil untuk jadwal tick berikutnya, sehingga rata-rata itu
mendekati sasaran: frame tiba `TargetSlackFraction` (0,5) dari interval **sebelum** tick yang memakainya. Di titik itu jitter ±½ interval
(±8 ms pada 60 fps) tidak lagi membuat tick kosong atau ganda.

- `FramePump` menerapkan koreksi: `next += interval + koreksi` (geser jadwal, bukan mengubah laju: koreksi ≤ 0,5 ms per tick, 0 di dalam deadband 0,5 ms).
- Pengukuran lama diputar ulang (dikurangi koreksi) setiap koreksi diterapkan, supaya jendela selalu mencerminkan hubungan fase saat ini
  (tanpa itu jendela yang tertinggal membuat pengendali melampaui sasaran dan berosilasi).
- Rata-rata melingkar (sin/cos), bukan linear, karena fase membungkus di batas tick.
- Tidak mengunci bila koherensi rendah (R < 0,5: frame tiba tanpa pola terhadap tick, mis. laju capture ≠ laju keluaran) → koreksi 0.
- Jeda publish lebih dari 250 ms (halaman diam lalu bergerak; jadwal capture dipasang ulang di titik baru) → jendela dikosongkan dan dikunci ulang
  setelah 8 pengukuran (sekitar 0,15 detik).
- Ganti fps (`SetPatternFormat`) → reset.
- Halaman diam: tidak ada publish, tidak ada koreksi; hub tetap mengulang frame terakhir pada laju penuh seperti sekarang.

Latensi: frame menunggu rata-rata ½ interval (8,3 ms pada 60 fps) sebelum dikirim. Pada fase acak lama rata-ratanya juga ½ interval
(seragam 0–16,7 ms), jadi latensi rata-rata sama tetapi kini konstan; dibanding fase yang kebetulan paling baik, +8 ms. Sasaran dapat
diturunkan (mis. 0,35) bila pengukuran menunjukkan margin lateness cukup.

## Alternatif yang dipertimbangkan

- **Tick dipicu oleh publish** (hub mengirim begitu frame siap, dengan batas waktu untuk halaman diam): latensi terendah, tetapi laju keluaran
  mengikuti jitter capture dan halaman diam butuh timer cadangan; mengubah arti `FramePump`. Ditolak sebagai lebih invasif.
- **Mengubah jadwal capture agar mengikuti pump**: capture dipilih dari frame compositor ~7 ms sekali, jadi presisinya sudah terbatas, dan readback
  async menambah jitter setelahnya. Menggeser tick lebih sederhana.

## Anti-tujuan

Laju keluaran tetap tepat fps (tidak ada tick tambahan atau hilang); preview tidak diturunkan; tidak ada gambar basi; halaman diam tetap penuh.
