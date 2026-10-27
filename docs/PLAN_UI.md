# Rencana UI/UX

Konteks: operator di event live, ruangan gelap, waktu terbatas, kesalahan langsung tampil di layar besar.
Karena itu UI harus cepat dibaca, sulit salah pencet, dan statusnya selalu terlihat.

## Prinsip

1. Tema gelap, kontras tinggi.
2. Status output selalu terlihat di bar atas.
3. Tombol besar untuk aksi yang sering: pindah tab, FPS, Blackout, Freeze.
4. Aksi berisiko diberi pengaman (konfirmasi saat menghentikan output live atau mengganti resolusi).
5. Tanpa mode kunci. Perubahan yang bisa memutus output (resolusi, FPS), mematikan output, dan test tone ke NDI/OMT yang sedang nyala meminta konfirmasi singkat; sisanya langsung bekerja.

## Tata letak jendela kontrol

```
┌─────────────────────────────────────────────────────────────────┐
│ ● LIVE  Monitor 2 ▪ NDI: Videotron ▪ OMT: Videotron  60fps 2520×1080 │
├──────────┬──────────────────────────────────────┬───────────────┤
│[Langsung │ [← →] [ URL ............ ] [↻]       │ RENDER        │
│ |Preview+│ ┌──────────────────────────────────┐ │ 2520 × 1080   │
│  Take]   │ │      PREVIEW (rasio asli)        │ │ preset, FPS   │
│ ▶ Opening│ └──────────────────────────────────┘ │ OUTPUT        │
│   Sponsor│ [TAKE] [ BLACKOUT ]  [ FREEZE ]      │ Layar/NDI/OMT │
│   Video  │                                      │ ▸ AUDIO       │
│ [+ Tab]  │                                      │ ▸ SESI LOGIN  │
├──────────┴──────────────────────────────────────┴───────────────┤
│ fps │ dibuang │ CPU │ GPU                                       │
└─────────────────────────────────────────────────────────────────┘
```

- Kiri: daftar tab (penanda tab live, ikon speaker untuk tab bersuara).
- Tengah: address bar + preview dengan rasio asli (2520x1080 tampil panjang, bukan dipaksa 16:9).
- Kanan: resolusi, FPS, output (monitor, NDI, OMT), audio.
- Bawah: statistik teknis.

## Alur

- Pertama kali: wizard singkat (pilih monitor, isi resolusi, nama sumber NDI/OMT).
- Sebelum acara: pilih profil (semua tab, resolusi, output), tekan "Mulai Output".
- Saat acara: pindah tab via klik atau Ctrl+1..9; Blackout dan Freeze sebagai tombol darurat.
- Setelah acara: "Hentikan Semua" dengan konfirmasi.

## Kondisi gagal

- Monitor dicabut: peringatan merah, output berhenti rapi, tombol "Pilih ulang monitor".
- Tab crash: tab ditandai, dimuat ulang otomatis, operator diberi tahu.
- NDI runtime / vMix tidak terdeteksi: pesan jelas tentang yang kurang.
- Frame mulai dibuang: indikator kuning di bar status.
- Sesi login habis di tab yang tayang: halaman login muncul di output. Operator menekan Freeze, login ulang (interaksi tetap jalan), lalu melepas Freeze. Peringatan otomatis untuk kasus ini belum dirancang; deteksinya heuristik [U].

## Kontrol halaman dan tampilan diperbesar

Preview kecil tidak cukup untuk mengoperasikan halaman, jadi ada toolbar di atas preview. Ada dua jenis zoom yang sengaja dipisah:

| Kontrol | Mengubah | Output ikut berubah? |
|---|---|---|
| Fit / 100% (zoom tampilan) | Cara preview digambar | Tidak |
| HALAMAN − 100% + (zoom halaman, di menu …) | Ukuran isi halaman | **Ya** |

- **Interaksi NYALA/MATI:** klik, scroll, dan ketik diteruskan ke halaman. Saat MATI, preview hanya melihat sehingga tidak ada klik tak sengaja. Saat NYALA ada penanda INTERAKSI, kursor berubah, dan Esc melepas keyboard.
- **Perbesar:** menyembunyikan panel kanan sehingga preview mengisi lebar jendela; Blackout, Freeze, dan status tetap terlihat.
- **Jendela terpisah:** preview yang sama di jendela sendiri, bisa diubah ukurannya dan dipindah ke monitor kontrol.
- **Zoom 100%:** satu piksel layar sama dengan satu piksel render. Bagian di luar bingkai digeser dengan Space + seret, dengan mini-map penunjuk posisi.
- **Navigasi:** Kembali, Maju, dan Muat ulang di baris alamat.
- Teknis: pemetaan koordinat dari preview ke viewport harus akurat di semua level zoom (diuji di spike S10). Keyboard di composition hosting belum terbukti [U].

## Skala layar (output HDMI)

Monitor/HDMI hanya 1920x1080, sedangkan render 2520x1080. Pengaturan **Skala ke monitor** ada di kartu **Layar penuh** (bersama pilihan monitor) dan memilih cara gambar masuk ke monitor:

- **Peras** (default): 2520x1080 diperas ke 1920x1080 (lebar 76,2%, tinggi 100%). UI menampilkan juga angka untuk Resolume/vMix: skalakan lebar ke 131,25%.
- **Fit:** proporsi asli, dengan pita hitam.
- **1:1:** piksel apa adanya, bagian luar monitor terpotong.
- Teks bantu berubah mengikuti resolusi dan mode, sehingga operator tidak perlu menghitung sendiri.
- Baris NDI dan OMT menampilkan resolusi kirim (2520x1080 asli).
- Wizard monitor menampilkan resolusi monitor (1920x1080), resolusi render, dan pilihan mode skala.

## Saat Freeze atau Blackout

Keduanya hanya mengubah apa yang **dikirim ke output** (HDMI, NDI, OMT). Capture dan input halaman tetap berjalan, jadi halaman tetap bisa dioperasikan. Kegunaannya: menyiapkan slide atau video berikutnya sementara penonton melihat gambar beku atau hitam.

- **Preview di UI tetap menampilkan halaman yang berjalan**, tidak ditutup hitam.
- **Kotak "KELUARAN"** kecil di pojok preview menunjukkan apa yang sedang dilihat penonton (gambar beku, atau hitam). Operator selalu tahu beda antara layar kerja dan layar tayang.
- Pindah tab saat Freeze: keluaran tetap beku pada tab awal; baru berpindah saat Freeze dilepas.
- Teks di bawah preview menjelaskan: "Halaman tetap berjalan dan bisa dioperasikan. Keluaran beku sampai dilepas."
- Tombol Interaksi tetap bisa dipakai; status bar dan bingkai tally kuning menandai bahwa keluaran sedang ditahan.
- **Risiko yang dijaga:** operator lupa Freeze masih aktif lalu mengira penonton melihat halaman terbaru. Karena itu label status, bingkai kuning, dan kotak KELUARAN muncul bersamaan.
- **Keputusan: audio ikut beku saat Freeze.** Audio yang dikirim ke output (lokal, NDI, OMT) menjadi senyap selama Freeze, dan kembali langsung (live, bukan yang tertinggal) saat Freeze dilepas. Audio tidak diulang dari buffer karena terdengar seperti rusak. Header AUDIO menampilkan "SENYAP · FREEZE" dan meter turun ke nol. Blackout tidak mengubah audio.

## Nama sumber NDI dan OMT

- Nama sumber ada di dalam **kartu output** NDI dan OMT (lihat "Crosscheck UI/UX"): kartu tertutup secara default, dibuka dengan klik pada barisnya, lalu kolom **Nama sumber** langsung bisa diedit. Ikon pena yang sebelumnya dipakai agar sidebar tidak penuh digantikan kartu yang bisa dibuka, karena kartu menyelesaikan masalah yang sama untuk semua pengaturan output, bukan hanya nama. Nama ini tampil di daftar sumber penerima (vMix, NDI Monitor, dan sejenisnya) dan ikut tersimpan di profil.
- Chip di bar atas dan teks detail langsung mengikuti nama yang diketik.
- **Mengubah nama saat output NYALA memutus sumber sesaat**, karena nama dipakai saat sender dibuat dan sender dibuat ulang. Kolom memberi tahu hal ini, dan di vMix sumbernya perlu dipilih ulang. Sebaiknya nama diatur sebelum acara.
- Validasi: nama tidak boleh kosong (pesan jelas, bukan hanya warna). Batas panjang dan karakter terlarang diperiksa dari dokumentasi NDI dan OMT saat implementasi [U].
- Di penerima, NDI biasanya menampilkan nama sumber bersama nama komputer, misalnya `NAMA-PC (Videotron-Main)` [U: konfirmasi dengan NDI Monitor].

## Nama tab

- Setiap tab punya ikon pensil di kanan barisnya. Klik, lalu baris berubah menjadi kolom nama dengan tombol centang untuk selesai. Hanya satu tab diedit pada satu waktu.
- Nama tab hanyalah **label untuk operator** (maksimal 40 karakter). Nama ini tidak mengubah halaman, dan tidak sama dengan nama sumber NDI/OMT.
- Tidak boleh kosong; pesannya berupa teks, dan tombol centang tidak menutup kolom sebelum nama terisi.
- Nama tersimpan di profil bersama URL dan urutan tab.
- Pintasan F2 untuk mengganti nama tab terpilih direncanakan di aplikasi (di mockup tidak dipasang karena aturan canvas melarang handler keyboard global). Apakah F2 bentrok dengan pintasan lain perlu dicek saat implementasi [U].
- Panel tab dilebarkan menjadi 300px agar nama tab dan ikon pensil muat bersama thumbnail.

## Perlindungan perubahan (tanpa mode kunci)

Keputusan 2026-10-27: tidak ada mode Setup/Show. Alasannya, kunci total terlalu kaku (NDI yang putus tidak bisa dinyalakan lagi, setelan kecil perlu langkah membuka kunci), sedangkan bahaya yang sebenarnya hanya satu: perubahan yang memutus output di depan penonton. Perlindungannya dipasang di tempat bahayanya, bukan lewat mode:

| Aksi | Perilaku |
|---|---|
| Ubah resolusi (preset atau ketik lalu Terapkan) | Bila ada output nyala: spanduk kuning "Mengubah resolusi saat tayang bisa memutus output beberapa detik" dengan Batal / Terapkan. Bila semua output mati, langsung berlaku. |
| Ubah FPS | Sama seperti resolusi. |
| Matikan output (Layar, NDI, OMT) | Selalu minta konfirmasi ("Matikan?"). Menyalakan tidak. |
| Mulai test tone ke NDI/OMT yang sedang nyala | Minta konfirmasi, karena tone terdengar di penerima. Tone ke Lokal langsung. |
| Ganti nama sumber NDI/OMT saat nyala | Kolom memberi peringatan bahwa sumber terputus sesaat dan sender dibuat ulang. |
| Hapus data login, hapus preset | Konfirmasi (dampaknya tidak bisa dibatalkan). |
| Skala layar, Selalu di atas, monitor, Profil terpisah, nama tab, zoom, Freeze/Blackout | Langsung. |

- Resolusi yang diketik tidak berlaku sampai tombol **Terapkan** ditekan, sehingga tidak ada perubahan di tengah pengetikan.
- Risiko yang sengaja diterima: salah klik pada setelan tanpa konfirmasi (skala layar, monitor) tetap mungkin. Dampaknya terlihat langsung dan bisa dibalik.
- Pertanyaan "kunci otomatis setelah 2 menit" tidak berlaku lagi.

## Crosscheck UI/UX (2026-10-27)

### Sudah diterapkan di mockup

**Pengaturan output disatukan.** Sebelumnya pengaturan output tersebar di empat tempat: bagian Resolusi, daftar Output, blok Skala Layar yang menempel di bawahnya, dan pilihan monitor yang hanya ada di wizard. Sekarang modelnya dua lapis:

- **RENDER (untuk semua output):** resolusi, preset, FPS. Diatur sekali.
- **OUTPUT:** tiga kartu yang bisa dibuka, masing-masing punya tombol NYALA/MATI dan pengaturannya sendiri.
  - **Layar penuh:** pilihan monitor, skala (Peras/Fit/1:1), angka untuk Resolume/vMix, dan **Selalu di atas** (NYALA/MATI, default nyala; keterangannya berubah mengikuti pilihan).
  - **NDI:** nama sumber, resolusi kirim (native), kirim audio.
  - **OMT:** sama dengan NDI.
- **AUDIO:** perangkat lokal, level, volume, Mute.

Temuan di baliknya:

| Temuan | Tindakan |
|---|---|
| Pilihan monitor tidak bisa diubah dari layar utama (hanya di wizard) | Ditambahkan di kartu Layar penuh |
| Tidak ada tombol untuk mengirim audio ke NDI/OMT, padahal ada di rencana | Ditambahkan di kartu NDI dan OMT |
| Panel kanan terlalu tinggi: sekitar 900px untuk ruang sekitar 796px (hitungan, belum diukur di layar), sehingga bagian Audio terpotong | Kartu tertutup secara default dan panel bisa di-scroll |
| Ikon pena dan tombol centang memakai pola yang hanya melayani satu kolom | Diganti kartu yang bisa dibuka |
| Mode Perbesar setinggi 536px melewati ruang yang tersedia sekitar 2px | Diturunkan ke 520px |

### Usulan penyederhanaan (diputuskan 2026-10-27)

Status: **1** diganti keputusan menghapus mode Setup/Show sama sekali (lihat "Perlindungan perubahan"). **2-7 diterapkan** di mockup. **8** dipertahankan: pil LIVE/PREVIEW berupa teks, sedangkan titik merah saja hanya mengandalkan warna.

1. **Satu tombol kunci menggantikan Setup/Show.** "Show" mirip "LIVE" sehingga membingungkan: yang satu mode kunci, yang lain status tayang. Spanduk kuning, segmen Setup/Show, dan konfirmasi buka kunci bisa diganti satu tombol gembok di bar atas (klik saat terkunci memunculkan konfirmasi; klik saat terbuka langsung mengunci).
2. **Chip di bar atas diringkas.** Sekarang memuat label, titik, dan nama, padahal nama sudah ada di kartu output. Cukup label + titik, dan titik menunjukkan kesehatan (hijau ok, merah error, abu mati).
3. **Hapus "NDI ok / OMT ok" di bar bawah**, karena duplikat dengan titik di chip. Bar bawah cukup fps, frame dibuang, CPU, GPU.
4. **Hapus tombol "Muat"** di baris alamat. Enter di kolom alamat sudah memuat, dan ada tombol Muat ulang.
5. **Zoom tampilan: Fit | 100% saja.** Pada 50% gambar 2520 piksel tetap terpotong (1260 piksel melebihi lebar bingkai), sehingga 50% hampir tidak memberi nilai tambah.
6. **Pindahkan "HALAMAN − 100% +" ke menu lebih** (…), karena jarang dipakai dan menghabiskan lebar toolbar.
7. **Hapus teks petunjuk di dasar panel tab** ("Ctrl+1 sampai Ctrl+4…"). Pindahkan ke tooltip atau layar bantuan; label LIVE di tab sudah menjelaskan keadaan.
8. **Label LIVE di tab** hanya membedakan diri dari tab terpilih saat Freeze/Blackout; di luar itu sama dengan sorotan tab terpilih. Pertahankan, atau ganti titik merah kecil pada tab yang benar-benar tayang.

### Yang sengaja dipertahankan

- **Perbesar dan Jendela terpisah** terlihat mirip, tapi untuk situasi berbeda (satu monitor vs dua monitor).
- **Status LIVE, bingkai tally, dan label KELUARAN** tampil bersamaan; ini pengulangan yang disengaja agar Freeze tidak terlewat.
- **Zoom tampilan dan zoom halaman** dipisahkan karena efeknya berbeda (UI saja vs ikut ke output).

## Keputusan: pindah tab

Dua cara pindah tab, dipilih dengan saklar di atas daftar tab:

- **Langsung:** klik tab = output berubah. Sederhana.
- **Preview + Take:** klik tab hanya menampilkannya di preview besar (bingkai hijau, label PREVIEW) sehingga bisa disiapkan dan dioperasikan tanpa terlihat penonton. Tombol **TAKE** (atau Spasi) memindahkan tab itu ke output. Kotak **PROGRAM** kecil di pojok preview menunjukkan terus apa yang sedang tayang (bingkai merah).

Rincian Preview + Take:

- **Take = tukar (swap):** tab preview menjadi program, tab program lama menjadi preview. Mudah kembali ke tab sebelumnya dengan TAKE lagi. Transisi awal: potong langsung (cut). Fade butuh komposit dua frame sekaligus, ditunda.
- **Tanda di daftar tab:** pil LIVE merah untuk program, pil PREVIEW hijau untuk preview (konvensi tally: merah tayang, hijau siap).
- **Kotak PROGRAM menggantikan kotak KELUARAN lama:** satu komponen yang sama. Saat Freeze atau Blackout, isinya berubah menjadi gambar beku atau hitam dengan bingkai kuning, sehingga tidak ada dua penanda terpisah.
- **Interaksi mengarah ke tab preview.** Chip bertuliskan "INTERAKSI · PREVIEW". Mengoperasikan tab yang sedang tayang (misalnya lanjut ke slide berikutnya) belum tersedia dari layar ini; pertanyaan terbuka di bawah.
- **TAKE nonaktif** bila preview sama dengan program.
- **Audio hanya dari tab program;** tab preview selalu dibisukan (lihat batasan di rencana).
- Resolusi dan FPS tetap global, sehingga preview persis seperti yang akan tampil.

**Keputusan: tab yang sedang tayang tidak boleh dioperasikan dalam mode Preview + Take.** Kotak PROGRAM hanya untuk dilihat; klik, scroll, dan ketik hanya masuk ke tab preview. Konsekuensinya: untuk melanjutkan slide atau video di tab yang tayang, pindah ke mode Langsung, atau TAKE-kan dulu (tab itu jadi preview lagi). Ini mencegah klik tak sengaja di halaman yang sedang dilihat penonton.

## Jadwal

- Spike S0-S9 memakai UI minimal.
- Desain UI final sebelum A2; implementasi bertahap bersama tiap modul.
- Preview interaktif (mockup HTML) dibuat sebagai artifact untuk disetujui sebelum A2.

## Arah visual (anti "AI slop")

Hasil riset (daftar tanda visual desain buatan AI + panduan craft) diterapkan sebagai berikut:

- **Font:** Barlow (UI, akrab di perangkat video/signage) + IBM Plex Mono (angka, resolusi, URL). Inter/Geist dihindari karena terlalu umum.
- **Warna bermakna, bukan hiasan:** latar abu-hangat hampir hitam (bukan slate-biru), tanpa gradien dan tanpa aksen biru/ungu.
  Warna hanya untuk status mengikuti konvensi tally: merah = LIVE/gagal, kuning = peringatan/Blackout/Freeze, hijau = aktif/ok. Hijau juga dipakai untuk "preview" pada mode Preview + Take.
  Elemen terpilih dan aksi utama memakai isian terang (teks gelap), bukan warna.
- **Palet (oklch, kontras sudah dihitung):** bg `#0C0B09`/`#151411`/`#1F1E1B`, garis `#34332F`/`#4A4743`, teks `#F0EEEA`, teks redup `#AEAAA4` (7.2:1 s.d. 8.5:1), live `#C50516` (putih di atasnya 6.2:1), kuning `#F7C243`, hijau `#56DC85`.
- **Bentuk:** sudut kecil (2-3px), panel dipisah garis tipis, bukan kartu bulat dengan bayangan.
- **Status tidak hanya lewat warna:** tombol output bertuliskan NYALA/MATI; label LIVE/BLACKOUT/FREEZE berupa teks.
- **Fungsi mendorong bentuk:** thumbnail per tab (dengan rasio resolusi aktif), bingkai tally di sekeliling preview (merah saat live, kuning saat Blackout/Freeze), denah monitor berskala asli di wizard, daftar masalah bergaya log (bukan grid kartu sama rata).
- **Tanpa isian palsu:** tidak ada statistik karangan atau dekorasi grafik; hanya angka yang memang akan ada (fps, frame dibuang, CPU, GPU).
- **Tanpa chrome palsu:** jendela asli Windows digambar oleh sistem, jadi mockup hanya menampilkan area klien.
- **Operator bisa menyesuaikan:** riset ruang kontrol menyarankan tema gelap untuk ruangan redup, tetapi kecerahan/kontras sebaiknya bisa diatur (opsi di Pengaturan, bukan di layar utama). Semua kontrol bisa dioperasikan dengan keyboard, sehingga pintasan mudah ditambahkan.

## Teknis (usulan)

- WPF dengan tema gelap sendiri; pustaka tema pihak ketiga dipertimbangkan belakangan [U: cek lisensi].
- Hotkey global untuk pindah tab dan Blackout bila jendela kontrol tidak fokus [U: perlu diuji].


## Test tone (audio)

Bagian AUDIO punya dropdown TEST TONE (tertutup secara default; header menampilkan status AKTIF/MATI): target (Lokal/NDI/OMT, bisa lebih dari satu), sinyal (sinus 1 kHz / pink noise), kanal (L+R/Kiri/Kanan), tombol MULAI/HENTIKAN TONE. Level tetap -20 dBFS, berhenti otomatis 30 detik, audio halaman dibisukan selama tone, meter menampilkan level tone. Memulai tone ke NDI/OMT yang sedang nyala meminta konfirmasi, agar tidak bocor ke program tanpa sengaja.


## Sesi login

Dropdown SESI LOGIN di panel kanan (tertutup default; header hanya menampilkan "N PERLU LOGIN" bila ada, selain itu kosong). Isi: baris per tab dengan status (SESI AKTIF / PERLU LOGIN / TANPA LOGIN, selalu berupa teks; tab yang belum dicek tidak berlabel) dan tombol Profil bersama/terpisah; tombol Cek sesi sebelum acara; Hapus data login dengan konfirmasi. Catatan: password tidak disimpan, login di Preview, Freeze dulu bila mode Langsung. Hasil cek di mockup adalah data contoh.


## Preset resolusi

Di bagian RENDER, lebar dan tinggi bisa diketik (pesan merah bila di luar 100-8192). Baris PRESET punya tombol **Kelola**. Di luar mode Kelola: klik preset = pakai; tombol "+ Simpan W x H sebagai preset" membuka isian nama (nama awal "Custom WxH", validasi nama kosong/kembar dan resolusi yang sudah ada). Dalam mode Kelola: preset buatan sendiri berlabel HAPUS dan memunculkan konfirmasi; preset bawaan berlabel BAWAAN dan nonaktif. Menerapkan resolusi baru meminta konfirmasi bila ada output nyala. Lebar/tinggi yang diketik berlaku setelah tombol Terapkan.

## Audio (satu kelompok)

AUDIO kini dropdown tertutup default; satu kelompok berisi perangkat audio, meter, volume, Mute, dan sub-dropdown Test tone. Header hanya menampilkan peringatan (MUTED atau TONE AKTIF, kuning) dan kosong saat normal.
