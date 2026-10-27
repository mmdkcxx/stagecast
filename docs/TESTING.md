# Strategi pengujian

Tujuan: menangkap kesalahan lewat otomatis sebanyak mungkin, tetapi **dijalankan per kelompok fase, bukan setiap fase**, supaya hemat waktu dan biaya. Tes ditulis bersama kodenya di tiap fase; yang ditunda hanya waktu menjalankannya.

## Empat lapis

| Lapis | Apa | Cara jalan | Siapa |
|---|---|---|---|
| **Typecheck** | Build penuh dengan analisis nullable dan semua peringatan kompiler dianggap error (`TreatWarningsAsErrors` di `Directory.Build.props`) | `scripts/check.sh typecheck` | Saya |
| **Unit** | xUnit untuk logika murni di `src/Stagecast.Core` (skala anamorfik, batas resolusi, aturan preset, dan seterusnya). Tanpa jendela, tanpa jaringan | `scripts/check.sh unit` | Saya |
| **Smoke** | Menjalankan exe spike/aplikasi yang menutup dirinya sendiri, lalu memeriksa isi log (runtime ada, navigasi sukses, tidak ada `FAILED`). Membuka jendela beberapa detik dan butuh internet | `scripts/check.sh smoke` | Saya |
| **Manual** | Hal yang hanya bisa dinilai dengan melihat atau mendengar (ketajaman, kelancaran, suara, kesan visual). Ditandai `[Anda]` di `PLAN_PHASES.md` | daftar periksa per kelompok | Anda |

`scripts/check.sh all` menjalankan tiga lapis pertama berurutan. Typecheck selalu jalan lebih dulu; kalau gagal, lapis berikutnya tidak berarti.

## Kelompok uji (sekali per kelompok)

| Kelompok | Fase | Dijalankan |
|---|---|---|
| 0 | S0-S1 (skeleton, WebView2 biasa) | **sudah, 2026-10-27**: typecheck lulus, 35 tes unit lulus, smoke lulus |
| 1 | S2-S5 (composition hosting, viewport, capture, 30/60 fps) | setelah S5 (S2 dan S3 sudah dicoba sekali saat ditulis: lulus) | **Dijalankan 2026-10-27: typecheck lulus, 45 unit test lulus, smoke s1-s5 lulus.**
| 2 | S6-S9 (konten nyata, GPU, audio loopback, dua WebView) | setelah S9 | **Dijalankan 2026-10-27 (sekaligus S10): typecheck lulus, 60 unit test lulus, smoke s1-s6, s8-s10 lulus (S7 tanpa smoke: ukur manual via scripts/gpu-compare.ps1).**
| 3 | S10-S12 + GATE (kontrol halaman, skala anamorfik, preview bersamaan) | sebelum GATE |
| 4 | A1-A2d (fondasi, render, kontrol halaman, konfirmasi, preset) | setelah A2d |
| 5 | A3, A3c, A3b (tab, sesi login, Preview + Take) | setelah A3b |
| 6 | A4-A5 (layar penuh, audio, test tone) | setelah A5 |
| 7 | A6-A7 (NDI, OMT) | setelah A7 |
| 8 | A8-A9 (stabilitas, paket) | setelah A9, termasuk uji beban berjam-jam |

Pengecualian, jalankan di luar jadwal:
- **S4** (capture satu frame) adalah titik keputusan awal; hasilnya diperiksa langsung.
- Tes yang gagal atau perubahan yang menyentuh kode bersama banyak fase.
- Sebelum rilis atau tag.

Aturan commit: commit tiap fase selesai; hasil kelompok uji dicatat di `PLAN_PHASES.md` pada fase terakhir kelompok itu.

## Menulis tes

- Logika yang bisa dipisahkan dari UI dan Windows ditaruh di `Stagecast.Core` (target `net8.0` biasa) dan diuji dengan xUnit di `tests/Stagecast.Core.Tests`.
- Kode yang bergantung pada WebView2, DirectX, WASAPI, NDI, atau OMT tidak diuji unit; ia diuji lewat smoke (log) dan manual.
- Satu tes satu perilaku; nama tes menyebut perilakunya.
- Angka yang masih usulan (misalnya batas resolusi 100-8192) diuji sebagai spesifikasi sementara; tesnya diubah bersama angkanya setelah spike S3.

## Yang belum tercakup

- Tampilan UI di aplikasi nyata (belum ada). Mockup di `docs/design` tidak diuji otomatis.
- Typecheck hanya untuk C#. Sumber mockup (`.dc.html`) tidak ikut diperiksa.
