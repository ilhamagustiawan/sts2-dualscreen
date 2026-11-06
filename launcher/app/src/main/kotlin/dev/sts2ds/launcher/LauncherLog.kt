// LauncherLog — single global event log for the launcher UI. Any
// component (SteamSession, LauncherActivity itself) can append lines via
// [log]; screens subscribe via [addListener] and mirror them on screen.
//
// Why a manual pubsub instead of LiveData/Flow: we want this object
// reachable from anywhere (including non-Android code paths like
// background coroutines + JavaSteam callbacks) without forcing every
// caller to depend on lifecycle libraries. The total event volume is
// small (~10s of lines per session) so a CopyOnWriteArrayList is fine.
//
// NOTHING IDENTIFYING GOES IN HERE. Not the Steam account name, not a
// SteamID, not a token. This log is written to files that survive the
// process -- one of them on shared storage, where other software on the phone
// may be able to read it -- the log screen offers Share, Save and Copy, and
// the whole reason it exists is for people to send it to us when a build
// fails. So every line should be read as something the user is about to post
// in an issue. Say that a sign-in happened, which is what makes the log
// useful; say who it was for, and the user has published their Steam account
// name to get help with a compiler error.
//
// The account name still belongs on SCREEN, where it tells the person which
// account they are signed in as and goes no further: LauncherActivity's login
// status line and LoginActivity's success message both show it.

package dev.sts2ds.launcher

import android.content.Context
import android.util.Log
import dev.sts2ds.LogLevel
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.CopyOnWriteArrayList

object LauncherLog {
    private const val TAG = "Sts2Launcher"
    private const val MAX_LINES = 500

    // Newest at the end. Read-only snapshot returned to subscribers.
    private val buffer = CopyOnWriteArrayList<String>()
    private val listeners = CopyOnWriteArrayList<Listener>()
    private val timeFormat = SimpleDateFormat("HH:mm:ss", Locale.US)

    // ── the files ───────────────────────────────────────────────────────────
    //
    // The buffer above is memory, and memory is exactly what a bug report does
    // not have: the interesting failures happen during setup, the user closes
    // the app, and by the time anybody asks for the log it is gone. So every
    // line is also appended to a file, and the file is what the log screen
    // shows -- including the session that failed yesterday.
    //
    // Appended and closed per line rather than held open behind a buffer. The
    // whole point is to survive the process dying, and a buffered writer's
    // last few kilobytes are the ones that say why it died.
    //
    // Written TWICE, to two directories, because the two have different jobs:
    //
    //   filesDir            /data/data/<pkg>/files/launcher.log
    //                       Always writable, never unmounted, invisible to the
    //                       person holding the phone -- nothing on a stock
    //                       device can open /data/data. This is the copy the
    //                       log screen reads.
    //
    //   getExternalFilesDir /sdcard/Android/data/<pkg>/files/launcher.log
    //                       May be missing, and on Android 11+ is not
    //                       browsable by a third-party file manager, but it IS
    //                       reachable over USB and by anything with storage
    //                       access -- and it is the first place someone
    //                       looking for a log file thinks to look, which is
    //                       reason enough on its own.
    //
    // GameReset leaves this file alone: it names the directories it deletes,
    // and the log sits beside them rather than in one. A log is at its most
    // useful directly after the reset that a failed launch provoked.

    private const val LOG_NAME = "launcher.log"
    private const val MAX_FILE_BYTES = 256L * 1024L
    private const val MAX_FILE_LINES = 4000
    private val fileLock = Any()
    @Volatile private var sinks: List<File> = emptyList()

    /** Bytes appended by this process since [attach], for the trim below. */
    private var written = 0L

    /**
     * Lines logged before [attach] had a file to write them to.
     *
     * Capped, because a process that never attaches must not grow this
     * without bound, and held under [fileLock] so that a line cannot be
     * queued and lost while attach is draining.
     */
    private const val MAX_PENDING = 200
    private val pending = ArrayList<String>()

    /** Where the log lives, once [attach] has been called. */
    fun file(context: Context): File = File(context.filesDir, LOG_NAME)

    /** The copy a user can actually get at, when there is external storage. */
    fun externalFile(context: Context): File? =
        context.getExternalFilesDir(null)?.let { File(it, LOG_NAME) }

    /**
     * The game's logs, oldest first.
     *
     * A separate set of files written by the game's process, which is where
     * the engine, the compat pack and the game itself log.
     */
    fun gameLogs(context: Context): List<File> {
        // Written by the game's process: Godot's own log (GodotApp passes
        // --log-file) and whatever the compat pack and the game add beside it.
        val dir = File(context.filesDir, "logs")
        val logs = dir.listFiles { f -> f.isFile && f.name.endsWith(".log") }.orEmpty()
        return logs.sortedBy { it.lastModified() }
    }

    /**
     * Starts writing to disk. Safe to call more than once and from any process.
     */
    fun attach(context: Context) {
        synchronized(fileLock) {
            if (sinks.isNotEmpty()) return
            val header = "\n=== ${SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US).format(Date())} ===\n" +
                "${deviceSummary()}\n"
            val opened = ArrayList<File>(2)
            for (f in listOfNotNull(file(context), externalFile(context))) {
                try {
                    f.parentFile?.mkdirs()
                    trim(f)
                    f.appendText(header)
                    opened.add(f)
                } catch (t: Throwable) {
                    // External storage can be absent, full, or read-only, and
                    // none of that may cost us the internal copy.
                    Log.w(TAG, "could not open the log file at $f", t)
                }
            }
            if (opened.isEmpty()) Log.w(TAG, "no log file could be opened; this session is logcat only")
            // Seeded from what is already there: the previous sessions still in
            // the file count against the cap, or a process that starts at 255 KB
            // writes another 256 before anything trims it.
            written = opened.firstOrNull()?.length() ?: 0L
            sinks = opened

            // Whatever was logged before there was anywhere to put it. Written
            // after the header so the session it belongs to is the one it is
            // filed under, and cleared either way: an unattachable process
            // must not accumulate these forever.
            if (opened.isNotEmpty()) {
                for (line in pending) {
                    try {
                        appendLocked(opened, line)
                    } catch (_: Throwable) {
                    }
                }
            }
            pending.clear()
        }
    }

    /**
     * Appends one line to every open sink and enforces the cap.
     *
     * Call under [fileLock]. Shared by [log] and by the pre-attach drain in
     * [attach], so that a buffered line is trimmed and counted exactly like a
     * live one.
     */
    private fun appendLocked(out: List<File>, stamped: String) {
        val line = "$stamped\n"
        for (f in out) f.appendText(line)

        // Enforced on the way out and not only at attach(): a long download
        // logs a lot in ONE process, so a cap applied only at startup is no
        // cap at all, and the file the log screen has to render would grow
        // without bound.
        written += line.length
        if (written > MAX_FILE_BYTES) {
            for (f in out) trim(f)
            written = out.firstOrNull()?.length() ?: 0L
        }
    }

    /**
     * Caps a log file by dropping the oldest lines. Call under [fileLock].
     *
     * Bounded by BYTES first and lines second, and cut back to half the cap
     * rather than to the cap. Trimming to exactly the limit would leave the
     * next line over it again, and this function reads and rewrites the whole
     * file -- doing that once per logged line is its own outage.
     *
     * The line cap is generous where the in-memory [MAX_LINES] is not: that
     * one backs a panel on a screen, this one backs the thing a user is asked
     * to send.
     */
    private fun trim(f: File) {
        if (!f.isFile || f.length() <= MAX_FILE_BYTES) return
        val budget = MAX_FILE_BYTES / 2
        val keep = ArrayDeque<String>()
        var bytes = 0L
        for (line in f.readLines().asReversed()) {
            bytes += line.length + 1
            if (bytes > budget || keep.size >= MAX_FILE_LINES) break
            keep.addFirst(line)
        }
        f.writeText(keep.joinToString("\n", postfix = "\n"))
    }

    /** Everything on disk, which is more than this session. */
    fun history(context: Context): String =
        try {
            file(context).takeIf { it.isFile }?.readText().orEmpty()
        } catch (t: Throwable) {
            ""
        }

    /**
     * The device, in one line.
     *
     * First thing in every log and first thing in every report, because it is
     * the question asked of every bug that only happens to somebody else.
     */
    fun deviceSummary(): String =
        try {
            val pageSize = android.system.Os.sysconf(android.system.OsConstants._SC_PAGESIZE)
            "device: ${android.os.Build.MANUFACTURER} ${android.os.Build.MODEL}, " +
                "Android ${android.os.Build.VERSION.RELEASE} (API ${android.os.Build.VERSION.SDK_INT}), " +
                "abis=${android.os.Build.SUPPORTED_ABIS.joinToString("/")}, " +
                "pageSize=$pageSize"
        } catch (t: Throwable) {
            "device: unknown (${t.message})"
        }

    fun interface Listener {
        fun onLog(line: String, full: List<String>)
    }

    /**
     * What reaches logcat and the files. Lines above it still reach the
     * launcher's status line through the in-memory buffer, and go no further.
     */
    @JvmField var level: LogLevel = LogLevel.BUILD

    /** A milestone or decision: logcat and the files from the devel build up. */
    fun log(message: String) = append(message, LogLevel.INFO)

    /** Per-event detail: logcat and the files in the debug build only. */
    fun debug(message: String) = append(message, LogLevel.DEBUG)

    /** Something went wrong: logcat and the files in every build. */
    fun warn(message: String) = append(message, LogLevel.WARN)

    private fun append(message: String, line: LogLevel, logcat: Boolean = true) {
        val stamped = "[${timeFormat.format(Date())}] $message"
        val persist = level.allows(line)
        if (logcat && persist) {
            when (line) {
                LogLevel.WARN -> Log.w(TAG, message)
                LogLevel.INFO -> Log.i(TAG, message)
                LogLevel.DEBUG -> Log.d(TAG, message)
            }
        }
        buffer.add(stamped)
        // Trim if needed. CopyOnWriteArrayList is O(n) on remove but
        // n stays small (capped at 500).
        while (buffer.size > MAX_LINES) buffer.removeAt(0)

        val out = sinks
        if (!persist) {
            // Kept in memory for the status line only.
        } else if (out.isNotEmpty()) {
            // A log line must never be the reason something fails, so a full
            // disk or a revoked directory is swallowed rather than thrown.
            try {
                synchronized(fileLock) {
                    appendLocked(sinks, stamped)
                }
            } catch (_: Throwable) {
            }
        } else {
            // No file yet. Held rather than dropped -- see [pending] -- and
            // re-checked under the lock, because attach() may have opened one
            // between the read above and here.
            try {
                synchronized(fileLock) {
                    val now = sinks
                    if (now.isNotEmpty()) appendLocked(now, stamped)
                    else if (pending.size < MAX_PENDING) pending.add(stamped)
                }
            } catch (_: Throwable) {
            }
        }

        val snapshot = buffer.toList()
        for (l in listeners) {
            try {
                l.onLog(stamped, snapshot)
            } catch (_: Throwable) {
                // Listener crashes shouldn't bring down whatever thread is logging.
            }
        }
    }

    /**
     * Logs a failure with its stack trace.
     *
     * The on-screen panel still gets one line -- it has no room for more --
     * but logcat gets the whole thing. A message alone is not enough to place
     * a failure inside a fetch that has a dozen steps and several archive
     * formats: "not in gzip format" is the same sentence wherever it came
     * from, and the frames are the only thing that says which one it was.
     */
    fun log(message: String, error: Throwable) {
        // A failure: kept in every build. The stack trace goes to logcat only.
        Log.w(TAG, message, error)
        append("$message: $error", LogLevel.WARN, logcat = false)
    }

    fun snapshot(): List<String> = buffer.toList()

    fun addListener(l: Listener) {
        listeners.add(l)
    }

    fun removeListener(l: Listener) {
        listeners.remove(l)
    }
}
