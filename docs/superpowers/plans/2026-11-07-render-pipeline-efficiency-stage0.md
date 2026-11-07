# Efisiensi jalur render: Implementation Plan (tahap 0 rinci, tahap 1–5 gerbang)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Membangun pengukuran yang bisa diulang untuk jalur halaman → capture → preview/output, mengambil baseline, lalu memperbaiki jalur itu tahap demi tahap dengan bukti angka.

**Architecture:** Metrik baru di dua sisi: `FrameCapturer` (waktu handler, tunggu lock, waktu preview) dan `PagePreview` (preview tampil/terlewat, lag), ditambah CPU per proses (Stagecast dan WebView2) dan GPU best-effort. Satu self test `perf` menjalankan skenario berfase dan menulis CSV; `scripts/perf.sh` menjalankannya. Bagian murni (statistik, sampler CPU, format CSV) ada di `Stagecast.Core` dengan unit test.

**Tech Stack:** C# .NET 8, WPF, WebView2 1.0.4314.35, Windows Graphics Capture + Vortice D3D11, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-07-render-pipeline-efficiency-design.md`

**Catatan:** Commit dibuat hanya saat pengguna memintanya, jadi rencana ini tidak punya langkah commit. Pengujian penuh dijalankan per batch (`scripts/check.sh`), typecheck selama membangun.

## Global Constraints

- Branch `beta`; `main` tidak disentuh.
- Perilaku yang terlihat pengguna tidak berubah pada tahap 0 (hanya menambah metrik).
- Hanya Windows; D3D11, Windows Graphics Capture, WebView2 tetap.
- Semua teks angka di CSV dan log memakai `CultureInfo.InvariantCulture`.
- Typecheck memakai `-warnaserror` (`scripts/check.sh typecheck`); nullable aktif.
- Skenario ukur yang sama dipakai sebelum dan sesudah tiap tahap (resolusi 2520×1080 @ 60, preview hidup).

## Review Focus

1. Belum ada frame sama sekali (statistik kosong): nol, bukan `NaN` atau exception. Diuji di `TimingStatsTests` dan `PerfCsvTests`.
2. Proses WebView2 berakhir saat sampling: diabaikan, tidak crash. Diuji di `ProcessCpuSamplerTests`.
3. `ResetStats` setelah ganti resolusi/fps: metrik baru ikut direset, tidak bercampur. Diuji di `TimingStatsTests` (Reset) dan dicek di log self test.
4. Penerima NDI/OMT nol: fase tetap berjalan dan mencatat `Connections = 0`. Diperiksa di self test `perf`.
5. Locale non-Inggris (koma desimal): CSV tetap titik desimal. Diuji di `PerfCsvTests`.

---

## Tahap 0: Pengukuran

### Task 1: `TimingStats` (statistik waktu aman-thread)

**Files:**
- Create: `src/Stagecast.Core/Metrics/TimingStats.cs`
- Test: `tests/Stagecast.Core.Tests/TimingStatsTests.cs`

**Interfaces:**
- Produces:
  - `public readonly record struct TimingSnapshot(long Count, double AverageMs, double P99Ms, double MaxMs)`
  - `public sealed class TimingStats { public void Add(double milliseconds); public TimingSnapshot Snapshot(); public void Reset(); }`

- [ ] **Step 1: Tulis tes** di `TimingStatsTests`: `Empty_snapshot_is_all_zero` (semua 0, Count 0); `Average_p99_and_max_of_1_to_100` (Add 1..100 → Count 100, AverageMs 50.5, MaxMs 100, P99Ms 100); `Max_and_average_stay_exact_beyond_the_sample_cap` (Add 25 000 nilai 1.0 lalu satu 9.0 → Count 25 001, MaxMs 9.0, AverageMs ≈ 1.0003); `Reset_clears_everything`; `Add_from_many_threads_counts_every_sample` (8 thread × 1000 → Count 8000).
- [ ] **Step 2: Implementasikan `TimingStats`** dengan lock sederhana; simpan hingga `MaxSamples = 20_000` sampel untuk P99 (pola sama dengan `FrameHub._tickIntervalsMs`), sedangkan Count, jumlah, dan max dihitung eksak tanpa batas. P99 = elemen terurut ke `min(n-1, (int)(n*0.99))`, 0 bila kosong.
- [ ] **Step 3: Typecheck:** `scripts/check.sh typecheck` → PASS.

### Task 2: `ProcessCpuSampler` dan `PerfCsv`

**Files:**
- Create: `src/Stagecast.Core/Metrics/ProcessCpuSampler.cs`, `src/Stagecast.Core/Metrics/PerfCsv.cs`
- Test: `tests/Stagecast.Core.Tests/ProcessCpuSamplerTests.cs`, `tests/Stagecast.Core.Tests/PerfCsvTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class ProcessCpuSampler { public ProcessCpuSampler(Func<int, TimeSpan?> readTotalCpu, int cores); public double Sample(IEnumerable<int> pids, TimeSpan wallElapsed); }` — hasil adalah persen total dari seluruh kapasitas CPU (0–100). Proses yang baru terlihat hanya menjadi acuan (tidak dihitung), proses yang `readTotalCpu`-nya `null` (sudah berakhir) diabaikan dan dilupakan.
  - `public sealed record PerfSample(string Phase, double Seconds, double OutFps, long Repeated, long Dropped, double CompositorFps, TimingSnapshot Copy, TimingSnapshot Handler, TimingSnapshot LockWait, TimingSnapshot PreviewCapture, double PreviewShownFps, double PreviewSkippedFps, TimingSnapshot PreviewLag, double TickP99Ms, double AppCpuPercent, double WebViewCpuPercent, double? GpuPercent, int NdiConnections, int OmtConnections)`
  - `public static class PerfCsv { public static string Header { get; } public static string Line(PerfSample s); }` — kolom sesuai urutan field (tiap `TimingSnapshot` menjadi `_avg`, `_p99`, `_max`; `Copy` memakai `copy_avg/copy_max` saja), titik desimal, `GpuPercent` null → kolom kosong.

- [ ] **Step 1: Tulis tes sampler:** `New_process_is_only_a_baseline` (sampel pertama 0); `Cpu_percent_is_delta_over_wall_over_cores` (pid 1: 1 s → 2 s CPU dalam 2 s wall, 4 core → 12.5); `Gone_process_is_ignored_and_forgotten` (reader mengembalikan null → tidak melempar, tidak dihitung, dan bila pid muncul lagi dianggap baru); `Sums_several_processes`.
- [ ] **Step 2: Tulis tes CSV:** `Header_and_line_have_the_same_number_of_columns`; `Uses_dot_decimals_under_a_comma_culture` (set `CultureInfo.CurrentCulture = new("id-ID")` di dalam tes, nilai 1.5 harus tampil `1.5`); `Null_gpu_is_an_empty_column`; `Empty_snapshots_write_zeros_not_NaN`.
- [ ] **Step 3: Implementasikan** kedua kelas. `Line` memakai `FormattableString.Invariant`/`CultureInfo.InvariantCulture`.
- [ ] **Step 4: Batch unit test:** `scripts/check.sh unit` → semua PASS, termasuk Task 1.

### Task 3: Metrik waktu di `FrameCapturer`

**Files:**
- Modify: `src/Stagecast.Rendering/Capture/FrameCapturer.cs` (`CaptureStats` baris 27, `OnFrameArrived`, `Stats`, `ResetStats`)

**Interfaces:**
- Consumes: `TimingStats`, `TimingSnapshot` (Task 1).
- Produces: `CaptureStats` mendapat parameter di akhir, dengan nilai bawaan agar pemanggil lama tetap jalan: `TimingSnapshot Handler = default, TimingSnapshot LockWait = default, TimingSnapshot PreviewCapture = default`.
  - `Handler`: durasi seluruh `OnFrameArrived` (dari masuk sampai selesai).
  - `LockWait`: waktu menunggu `_deviceGate` sebelum masuk.
  - `PreviewCapture`: durasi blok preview di dalam lock, mencakup `UpdatePreviewSource`, `ReadPreviewImage`, **dan** pemanggilan `PreviewReady` (yang membuat `BitmapSource` di thread capture).

- [ ] **Step 1:** Tambahkan tiga `TimingStats` privat; ukur dengan `Stopwatch.GetTimestamp()` di tiga tempat di atas (ukur `LockWait` sebagai selisih sebelum dan sesudah `lock (_deviceGate)`).
- [ ] **Step 2:** Isi ketiga snapshot di `Stats()` dan panggil `Reset()` ketiganya di `ResetStats()`.
- [ ] **Step 3: Typecheck** → PASS. (Tidak ada unit test: kelas ini butuh D3D; terbukti lewat self test Task 6.)

### Task 4: Metrik sisi preview (UI)

**Files:**
- Modify: `src/Stagecast.Rendering/Capture/FrameCapturer.cs` (`PreviewImage` baris 24, `ReadPreviewImage`), `src/Stagecast.App/PagePreview.xaml.cs` (`OnPreviewReady`, `ShowPending`), `src/Stagecast.App/MainWindow.xaml.cs` (akses via `Preview`)

**Interfaces:**
- Consumes: `TimingStats` (Task 1).
- Produces:
  - `PreviewImage` mendapat `long CapturedAt = 0` (nilai `Stopwatch.GetTimestamp()` saat gambar dibaca; diisi di `ReadPreviewImage`).
  - `public sealed record PreviewStats(double ShownPerSecond, double SkippedPerSecond, TimingSnapshot Lag)` di namespace `Stagecast.App`.
  - `PagePreview.PreviewStats Stats()` dan `PagePreview.ResetStats()` (hanya UI thread; `Interlocked` untuk hitungan yang diubah di thread capture).

- [ ] **Step 1:** Di `OnPreviewReady`, bila `Volatile.Write` menimpa `_pendingShot` yang belum tampil, tambah hitungan *skipped*. Di `ShowPending`, tambah hitungan *shown* dan catat lag = `Stopwatch.GetElapsedTime(image.CapturedAt)` ke `TimingStats`.
- [ ] **Step 2:** Implementasikan `Stats()`/`ResetStats()` dengan jam mulai sendiri (seperti `FrameCapturer`).
- [ ] **Step 3: Typecheck** → PASS.

### Task 5: Daftar proses WebView2

**Files:**
- Modify: `src/Stagecast.Rendering/RenderEngine.cs` (dekat `BrowserProcessIds`, baris 407)

**Interfaces:**
- Produces: `public IReadOnlyList<int> WebViewProcessIds()` — browser process setiap `PageTab` ditambah seluruh turunannya (renderer, GPU, utility), tanpa proses WebView2 milik aplikasi lain. UI thread saja.

- [ ] **Step 1:** Pakai `CoreWebView2Environment.GetProcessInfos()` bila tersedia di paket 1.0.4314.35 (kemungkinan besar ya); jika tidak, telusuri turunan dari `BrowserProcessIds` dengan Toolhelp snapshot (`CreateToolhelp32Snapshot`, `th32ParentProcessID`).
- [ ] **Step 2: Typecheck** → PASS. Dibuktikan di Task 6 (log fase `idle` harus menunjukkan jumlah proses > 0).

### Task 6: Self test `perf`, skrip, dan baseline

**Files:**
- Modify: `src/Stagecast.App/SelfTest.cs` (pemilihan nama dekat baris 54; fungsi baru dekat `RunSoakAsync` baris 878), `scripts/check.sh` (daftar nama self test di komentar), `docs/TESTING.md`
- Create: `scripts/perf.sh`, `docs/perf/baseline-2026-11-07.md`

**Interfaces:**
- Consumes: `CaptureStats` (Task 3), `PagePreview.Stats()` (Task 4), `RenderEngine.WebViewProcessIds()` (Task 5), `ProcessCpuSampler`, `PerfCsv`, `PerfSample` (Task 2), `window.Ndi?.Connections`, `window.Omt?.Connections`.
- Produces: `private async Task RunPerfAsync(MainWindow window)`; berkas `<log-dir>\perf-yyyyMMdd-HHmmss.csv`; satu baris log `PERF <fase> ...` per fase dan `PERF finished ok`.

Skenario (2520×1080 @ 60; halaman `Assets/selftest-soak.html` kecuali `STAGECAST_PERF_URLS` berisi daftar URL dipisah koma; preview hidup):

| Fase | Isi | Cuplikan |
|---|---|---|
| `idle` | `about:blank` | 1 baris, 10 detik |
| `load` | buka URL pertama | 1 baris per detik selama 20 detik (`load-1`…`load-20`), menangkap lonjakan saat halaman dibuka |
| `steady` | halaman sudah selesai | 1 baris, 30 detik |
| `ndi-0` | NDI nyala tanpa penerima | 1 baris, 20 detik |
| `ndi-1` | NDI dengan penerima (`NdiProbe`, seperti self test di baris 649) | 1 baris, 20 detik |
| `omt-0` | OMT nyala tanpa penerima | 1 baris, 20 detik |

- [ ] **Step 1:** Implementasikan `RunPerfAsync` mengikuti pola `RunSoakAsync` (`window.Confirm = _ => true`, `ApplyFromCode`, `NavigateAsync`). Setiap fase: `_engine.ResetStats()` + `window.Preview.ResetStats()`, tunggu, bentuk satu `PerfSample`, tulis `PerfCsv.Line`, log ringkas. CPU Stagecast dari `Process.GetCurrentProcess().TotalProcessorTime` lewat `ProcessCpuSampler`, WebView2 dari `WebViewProcessIds()`.
- [ ] **Step 2:** Daftarkan nama `perf` di pemilihan self test (`if (name == "perf") { await RunPerfAsync(window); return; }`).
- [ ] **Step 3:** Buat `scripts/perf.sh` meniru `scripts/soak.sh` (build Debug, data-dir bersih, `--selftest perf`, tampilkan baris `PERF` dan path CSV). Tambahkan `perf` ke daftar nama di komentar `check.sh` dan satu baris di `docs/TESTING.md`.
- [ ] **Step 4: Typecheck** → PASS, lalu `scripts/check.sh unit` → PASS.
- [ ] **Step 5: Jalankan** `scripts/perf.sh`. Harapan: berakhir dengan `PERF finished ok`, CSV punya satu baris per fase, `webview_cpu_percent` > 0 pada fase `load`, tidak ada `NaN`.
- [ ] **Step 6: Tulis `docs/perf/baseline-2026-11-07.md`**: tabel per fase (angka dari CSV), spesifikasi mesin, dan catatan suhu/kipas secara manual (HWiNFO atau sejenisnya bila ada; tidak diotomasi). Ini menjadi acuan semua tahap berikutnya.

### Task 7 (best-effort): Kolom GPU

**Files:**
- Modify: `src/Stagecast.App/Stagecast.App.csproj` (paket `System.Diagnostics.PerformanceCounter`), `src/Stagecast.App/SelfTest.cs`
- Create: `src/Stagecast.App/GpuUsage.cs`

**Interfaces:**
- Produces: `internal static class GpuUsage { public static double? Percent(IEnumerable<int> pids); }` — jumlah `GPU Engine\Utilization Percentage` untuk instance yang namanya memuat `pid_<pid>_`; `null` bila counter tidak ada atau gagal dibaca (tidak pernah melempar).

- [ ] **Step 1:** Implementasikan dengan `PerformanceCounterCategory("GPU Engine")`, bungkus semuanya dengan try/catch ke `null`.
- [ ] **Step 2:** Isi `GpuPercent` di `PerfSample` pada `RunPerfAsync` (proses Stagecast + `WebViewProcessIds()`), ulangi `scripts/perf.sh`, perbarui baseline. Bila selalu `null` di mesin ini, catat di baseline dan lanjut tanpa kolom GPU.

---

## Tahap 1–5 (gerbang: dirinci setelah baseline ada)

Tiap tahap dirinci menjadi rencana sendiri setelah angka baseline terbaca, karena urutan dan manfaatnya bergantung pada hasil ukur. Gerbang yang berlaku untuk semuanya: jalankan `scripts/perf.sh`, bandingkan dengan baseline, dan lanjut hanya bila ada perbaikan terukur tanpa melanggar anti-tujuan di spec.

| Tahap | Perubahan | Titik sentuh yang sudah diketahui |
|---|---|---|
| 1 | Lewati pekerjaan tak perlu: tanpa penerima NDI/OMT lewati readback dan kirim; halaman diam kirim ulang buffer terakhir; hentikan capture tab tersembunyi | `FrameCapturer.OnFrameArrived/PublishFull`, `NdiOutput.Connections`, `OmtOutput` penerima |
| 2 | Readback asinkron dengan staging ganda; preview keluar dari lock dan thread output, **termasuk pembuatan `BitmapSource` yang kini berjalan di dalam `_deviceGate`** | `FrameCapturer`, `PagePreview.OnPreviewReady` |
| 3 | UYVY di GPU saat `SendAlpha` mati | `NdiOutput` (`FourCC`), `OmtOutput` (`OMTCodec`) |
| 4 | Preview dari texture GPU (`D3DImage`) | `PagePreview`, `FrameCapturer.ReadPreviewImage` |
| 5 | Setelan WebView2 satu per satu | `RenderEngine` (pembuatan environment dan controller) |
| 1b (selesai) | Kunci fase tick hub ke jadwal publish capture (`PhaseLock`, `FrameHub.TakePhaseCorrectionMs`, `FramePump`); lihat `specs/2026-11-07-hub-phase-lock-design.md` dan Baseline 6 | `FrameHub.Publish/Tick`, `FramePump.Run` |
| B | HDMI langsung dari swapchain, hanya bila masih drop | `Display`/output layar |

## Self-Review

- **Cakupan spec:** tahap 0 (Task 1–7) dan semua tahap lain tercantum; tahap B sebagai gerbang. Metrik di spec (readback, frame terlewat preview dan output, stall, CPU/GPU per proses) masing-masing punya kolom. Suhu sengaja manual, sesuai spec ("bila tersedia").
- **Konsistensi tipe:** `TimingSnapshot` dan `TimingStats` (Task 1) dipakai di Task 2, 3, 4; `PerfSample` (Task 2) dipakai di Task 6–7; nama `WebViewProcessIds` konsisten di Task 5–6.
- **Proporsi:** tidak ada badan fungsi; hanya tanda tangan, nama tes, dan nilai yang ditentukan.
