// LauncherActivity — the app's entry point: sign in, download, launch.
//
// Modelled on SilksongAndroid's LauncherActivity and SetupActivity (MIT,
// https://github.com/jakobkhansen/SilksongAndroid): one panel of buttons
// beside a live mirror of the launcher log. The cloud-save flows below
// (pullFlow, pushFlow and the conflict prompt) are Silksong's, unchanged
// apart from names. What is new is the setup: there is nothing to compile
// for STS2, so "set up" is just the depot download, and launching runs
// GamePrep and starts the Godot activity.
//
// This activity runs in its own ":launcher" process, as Silksong's does:
// Godot ends its process when the game quits, and the launcher should still
// be there afterwards to upload saves.

package dev.sts2ds.launcher

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.os.Bundle
import android.view.View
import android.widget.Button
import android.widget.ProgressBar
import android.widget.ScrollView
import android.widget.TextView
import com.godot.game.GodotApp
import dev.sts2ds.R
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

class LauncherActivity : Activity() {

    companion object {
        /** Set by GodotApp when it bounced back here because the game was not ready. */
        const val EXTRA_ERROR = "launch_error"
        private const val REQ_LOGIN = 1001
        private const val MAX_LOG_LINES = 400
    }

    private val uiScope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private lateinit var tokenStore: TokenStore
    private lateinit var settings: SettingsStore
    private var creds: TokenStore.Credentials? = null
    private var cloudJob: Job? = null
    private var downloadJob: Job? = null
    private var returningFromGame = false

    private lateinit var txtStatus: TextView
    private lateinit var btnLaunch: Button
    private lateinit var btnDownload: Button
    private lateinit var progressDownload: ProgressBar
    private lateinit var btnLogin: Button
    private lateinit var btnPull: Button
    private lateinit var btnPush: Button
    private lateinit var spinCloud: ProgressBar
    private lateinit var txtLog: TextView
    private lateinit var scrollLog: ScrollView

    private val logListener = LauncherLog.Listener { _, full ->
        runOnUiThread { showLog(full) }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_launcher)
        tokenStore = TokenStore(this)
        settings = SettingsStore(this)
        creds = tokenStore.read()

        txtStatus = findViewById(R.id.txt_status)
        btnLaunch = findViewById(R.id.btn_launch)
        btnDownload = findViewById(R.id.btn_download)
        progressDownload = findViewById(R.id.progress_download)
        btnLogin = findViewById(R.id.btn_login)
        btnPull = findViewById(R.id.btn_pull)
        btnPush = findViewById(R.id.btn_push)
        spinCloud = findViewById(R.id.spin_cloud)
        txtLog = findViewById(R.id.txt_log)
        scrollLog = findViewById(R.id.scroll_log)

        btnLaunch.setOnClickListener { onLaunchClicked() }
        btnDownload.setOnClickListener { onDownloadClicked() }
        btnLogin.setOnClickListener { onLoginClicked() }
        btnPull.setOnClickListener { onPullClicked() }
        btnPush.setOnClickListener { onPushClicked() }
        findViewById<Button>(R.id.btn_settings).setOnClickListener {
            startActivity(Intent(this, SettingsActivity::class.java))
        }
        findViewById<Button>(R.id.btn_logs).setOnClickListener {
            startActivity(Intent(this, LogActivity::class.java))
        }

        LauncherLog.addListener(logListener)
        showLog(LauncherLog.snapshot())
        intent.getStringExtra(EXTRA_ERROR)?.let {
            LauncherLog.log("Game did not start: $it")
        }
        refreshUi()
    }

    override fun onDestroy() {
        LauncherLog.removeListener(logListener)
        uiScope.cancel()
        super.onDestroy()
    }

    override fun onResume() {
        super.onResume()
        refreshUi()
        // Only after a game session, not after Settings or a dialog.
        if (returningFromGame) {
            returningFromGame = false
            maybeAutoPush()
        }
    }

    private fun showLog(lines: List<String>) {
        txtLog.text = lines.takeLast(MAX_LOG_LINES).joinToString("\n")
        scrollLog.post { scrollLog.fullScroll(View.FOCUS_DOWN) }
    }

    // ── State ──────────────────────────────────────────────────────────

    private fun refreshUi() {
        val c = creds
        val hasGame = DepotLocation.present(this)
        val downloading = downloadJob?.isActive == true
        val busy = downloading || cloudJob?.isActive == true

        btnLogin.text = if (c == null) getString(R.string.action_log_in) else getString(R.string.action_log_in_as, c.accountName)
        btnLogin.isEnabled = !busy

        btnDownload.visibility = if (c != null) View.VISIBLE else View.GONE
        btnDownload.text = when {
            downloading -> getString(R.string.action_cancel_download)
            hasGame -> getString(R.string.action_update)
            else -> getString(R.string.action_download)
        }
        btnDownload.isEnabled = cloudJob?.isActive != true
        progressDownload.visibility = if (downloading) View.VISIBLE else View.GONE

        btnLaunch.isEnabled = hasGame && !busy
        btnLaunch.alpha = if (btnLaunch.isEnabled) 1f else 0.4f
        btnPull.isEnabled = c != null && !busy
        btnPush.isEnabled = c != null && !busy

        if (!downloading && cloudJob?.isActive != true) {
            txtStatus.text = when {
                hasGame -> GamePrep.version(this).let {
                    if (it.isEmpty()) getString(R.string.launcher_status_ready_unknown)
                    else getString(R.string.launcher_status_ready, it)
                }
                c == null -> getString(R.string.launcher_status_need_login)
                DepotLocation.interrupted(this) -> getString(R.string.launcher_status_interrupted)
                else -> getString(R.string.launcher_status_need_download, c.accountName)
            }
        }
        if (!hasGame && c != null) btnDownload.requestFocus() else if (hasGame) btnLaunch.requestFocus()
    }

    // ── Login ──────────────────────────────────────────────────────────

    private fun onLoginClicked() {
        if (creds != null) {
            AlertDialog.Builder(this)
                .setMessage("Log out of Steam? The downloaded game and your saves stay on this device.")
                .setNegativeButton(android.R.string.cancel, null)
                .setPositiveButton("Log out") { _, _ ->
                    LauncherLog.log("Logged out")
                    tokenStore.clear()
                    creds = null
                    refreshUi()
                }
                .show()
            return
        }
        @Suppress("DEPRECATION")
        startActivityForResult(Intent(this, LoginActivity::class.java), REQ_LOGIN)
    }

    @Deprecated("Activity result API is fine for a single request code")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        @Suppress("DEPRECATION")
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode != REQ_LOGIN) return
        if (resultCode != RESULT_OK || data == null) {
            LauncherLog.log("Login cancelled")
            return
        }
        val account = data.getStringExtra(LoginActivity.EXTRA_ACCOUNT)
        val token = data.getStringExtra(LoginActivity.EXTRA_TOKEN)
        if (account.isNullOrEmpty() || token.isNullOrEmpty()) {
            LauncherLog.log("Login returned without account/token")
            return
        }
        val newCreds = TokenStore.Credentials(account, token)
        tokenStore.write(newCreds)
        creds = newCreds
        LauncherLog.log("Steam credentials saved")
        refreshUi()
    }

    // ── Download ───────────────────────────────────────────────────────

    private fun onDownloadClicked() {
        downloadJob?.let {
            if (it.isActive) {
                LauncherLog.log("Download cancelled; it resumes where it stopped next time")
                it.cancel()
                return
            }
        }
        val c = creds ?: return
        val target = DepotLocation.gameDir(this)
        progressDownload.progress = 0
        downloadJob = uiScope.launch {
            try {
                DepotFetcher.download(c, target, DepotLocation.stagingDir(this@LauncherActivity)).collect { event ->
                    when (event) {
                        is DepotFetcher.Event.Progress -> {
                            // 0..1 across the depot.
                            val fraction = event.fraction.coerceIn(0f, 1f)
                            progressDownload.progress = (fraction * 1000).toInt()
                            txtStatus.text = getString(
                                R.string.launcher_status_downloading,
                                "%.1f%% · %s".format(fraction * 100, formatBytes(event.bytes)),
                            )
                        }
                        is DepotFetcher.Event.Status -> txtStatus.text = event.message
                        DepotFetcher.Event.Done -> LauncherLog.log("Game downloaded to $target")
                    }
                }
                // Stage once now so the first launch is quick and any problem
                // with the files shows up here rather than as a failed launch.
                withContext(Dispatchers.IO) {
                    GamePrep.prepare(this@LauncherActivity, force = true) { step ->
                        runOnUiThread { txtStatus.text = getString(R.string.launcher_status_preparing, step) }
                    }
                }
            } catch (_: kotlinx.coroutines.CancellationException) {
                // Cancelled from the button.
            } catch (t: Throwable) {
                LauncherLog.log("Download failed: ${t.message ?: t.javaClass.simpleName}", t)
            } finally {
                downloadJob = null
                refreshUi()
            }
        }
        refreshUi()
    }

    private fun formatBytes(bytes: Long): String = when {
        bytes >= 1L shl 30 -> "%.2f GB".format(bytes / (1L shl 30).toDouble())
        bytes >= 1L shl 20 -> "%.0f MB".format(bytes / (1L shl 20).toDouble())
        else -> "%d KB".format(bytes / 1024)
    }

    // ── Cloud saves ────────────────────────────────────────────────────

    private fun onPullClicked() {
        val c = creds ?: return
        runCloudJob { pullFlow(c, source = "manual") }
    }

    /**
     * Shared analyze + conflict-prompt + download pipeline used by the
     * Pull button and the pre-launch sync. Either pulls EVERYTHING the
     * analyzer flagged (safe set + conflicts when the user keeps remote)
     * or pulls NOTHING — never a per-slot mix.
     *
     * Returns true if the caller may proceed (synced, nothing to do, or
     * the conflict was resolved by keeping local/remote); false only
     * when the user cancelled an unresolved conflict — the pre-launch
     * path uses that to abort the game launch.
     */
    private suspend fun pullFlow(c: TokenStore.Credentials, source: String): Boolean {
        try {
            LauncherLog.log("Analyzing cloud vs local saves ($source pull)…")
            val analysis = CloudSync.analyzePull(this@LauncherActivity, c)

            if (analysis.toDownload.isEmpty() && analysis.conflicts.isEmpty()) {
                LauncherLog.log("Pull: nothing newer on cloud")
                return true
            }

            if (analysis.hasConflicts) {
                when (confirmConflictResolution(analysis.conflicts.map { it.localFile.name }, newerSide = "local")) {
                    ConflictChoice.KEEP_LOCAL -> {
                        LauncherLog.log("Conflict: keep local — pushing local saves over cloud")
                        pushLocalOverCloud(c)
                        return true
                    }
                    ConflictChoice.CANCEL -> {
                        LauncherLog.log("Pull cancelled by user (local is newer for ${analysis.conflicts.size} file(s))")
                        return false
                    }
                    ConflictChoice.KEEP_REMOTE -> {
                        // Fall through: download EVERYTHING flagged (safe +
                        // conflicts), clobbering the newer local copies with
                        // cloud. All-or-nothing — never a mix of slots.
                        LauncherLog.log("Conflict: keep remote — overwriting local with cloud")
                    }
                }
                CloudSync.pullItems(this@LauncherActivity, c, analysis.toDownload + analysis.conflicts).collect { }
            } else {
                CloudSync.pullItems(this@LauncherActivity, c, analysis.toDownload).collect { }
            }
            return true
        } catch (t: Throwable) {
            LauncherLog.log("Pull failed: ${t.message ?: t.javaClass.simpleName}")
            android.util.Log.e("Sts2Launcher.Cloud", "pull flow failed ($source)", t)
            return true
        }
    }

    private fun onPushClicked() {
        val c = creds ?: return
        runCloudJob { pushFlow(c) }
    }

    /** After a game session, when Settings → auto-push is on. */
    private fun maybeAutoPush() {
        val c = creds ?: return
        if (!settings.autoPush) return
        LauncherLog.log("Auto-push: starting (returned from game)")
        runCloudJob { pushFlow(c) }
    }

    private suspend fun pushFlow(c: TokenStore.Credentials) {
        try {
            LauncherLog.log("Analyzing local vs cloud saves…")
            val analysis = CloudSync.analyzePush(this@LauncherActivity, c)

            // Proceed when there's anything to do — uploads, conflicts,
            // OR cloud orphans to prune. Without the toDelete check we
            // bail out whenever nothing is queued for upload, so a
            // "delete-only" push (local pruned a rotated userN.dat.bakM
            // backup that cloud still holds, with no newer file to send)
            // would skip pushItems/toDelete entirely and leak the orphan.
            if (analysis.toUpload.isEmpty() &&
                analysis.conflicts.isEmpty() &&
                analysis.toDelete.isEmpty()
            ) {
                LauncherLog.log("Push: nothing to do (all ${analysis.skipped.size} local file(s) already match cloud, no orphans)")
                return
            }

            if (analysis.hasConflicts) {
                when (confirmConflictResolution(analysis.conflicts.map { it.localFile.name }, newerSide = "cloud")) {
                    ConflictChoice.KEEP_REMOTE -> {
                        LauncherLog.log("Conflict: keep remote — pulling cloud saves over local")
                        pullCloudOverLocal(c)
                        return
                    }
                    ConflictChoice.CANCEL -> {
                        LauncherLog.log("Push cancelled by user (cloud is newer for ${analysis.conflicts.size} file(s))")
                        return
                    }
                    ConflictChoice.KEEP_LOCAL -> {
                        // Fall through: upload EVERYTHING (safe + conflicts),
                        // clobbering the newer cloud copies with local.
                        LauncherLog.log("Conflict: keep local — overwriting cloud with local")
                    }
                }
                CloudSync.pushItems(c, analysis.all, toDelete = analysis.toDelete).collect { }
            } else {
                CloudSync.pushItems(c, analysis.toUpload, toDelete = analysis.toDelete).collect { }
            }
        } catch (t: Throwable) {
            LauncherLog.log("Push failed: ${t.message ?: t.javaClass.simpleName}")
            android.util.Log.e("Sts2Launcher.Cloud", "push flow failed", t)
        }
    }

    private enum class ConflictChoice { KEEP_LOCAL, KEEP_REMOTE, CANCEL }

    /**
     * Conflict resolver shown when local and cloud have diverged since
     * the last sync (one side is strictly newer than the other). Offers
     * a clear three-way choice instead of an ambiguous "overwrite?":
     *
     *   Keep local  → upload this device's saves, replacing the cloud.
     *   Keep remote → download the cloud saves, replacing this device.
     *   Cancel      → do nothing.
     *
     * Both directions are all-or-nothing — the winning side replaces the
     * other for the whole save set, never a per-slot mix. [newerSide] is
     * context for the message only ("the cloud copy is newer", etc.); the
     * choice itself is symmetric regardless of which button (Push or
     * Pull) opened the flow.
     */
    private suspend fun confirmConflictResolution(
        conflictFiles: List<String>,
        newerSide: String,
    ): ConflictChoice = kotlinx.coroutines.suspendCancellableCoroutine { cont ->
        fun finish(choice: ConflictChoice) {
            if (cont.isActive) cont.resumeWith(Result.success(choice))
        }
        val count = conflictFiles.size
        val msg = buildString {
            append("Your device and the cloud have both changed since the last sync ")
            append("(the $newerSide copy is newer).\n\n")
            append("Conflicting file(s):\n")
            for (name in conflictFiles.take(8)) append("• ").append(name).append('\n')
            if (count > 8) append("…and ").append(count - 8).append(" more\n")
            append("\nKeep local: upload this device's saves, replacing the cloud copy.\n")
            append("Keep remote: download the cloud saves, replacing this device's copy.")
        }
        android.app.AlertDialog.Builder(this@LauncherActivity)
            .setTitle("Save conflict")
            .setMessage(msg)
            .setPositiveButton("Keep local") { _, _ -> finish(ConflictChoice.KEEP_LOCAL) }
            .setNegativeButton("Keep remote") { _, _ -> finish(ConflictChoice.KEEP_REMOTE) }
            .setNeutralButton("Cancel") { _, _ -> finish(ConflictChoice.CANCEL) }
            .setOnCancelListener { finish(ConflictChoice.CANCEL) }
            .show()
    }

    /**
     * Forced full pull (no prompt): the user chose "keep remote", so
     * download the entire cloud set, clobbering local — including files
     * where local was newer. Re-analyses from scratch because the
     * originating flow may have been a push, which has no pull picture.
     */
    private suspend fun pullCloudOverLocal(c: TokenStore.Credentials) {
        val analysis = CloudSync.analyzePull(this@LauncherActivity, c)
        val items = analysis.toDownload + analysis.conflicts
        if (items.isEmpty()) {
            LauncherLog.log("Keep remote: nothing on cloud to pull")
            return
        }
        CloudSync.pullItems(this@LauncherActivity, c, items).collect { }
    }

    /**
     * Forced full push (no prompt): the user chose "keep local", so
     * upload the entire local set, clobbering cloud — including files
     * where cloud was newer. Re-analyses from scratch because the
     * originating flow may have been a pull, which has no push picture.
     */
    private suspend fun pushLocalOverCloud(c: TokenStore.Credentials) {
        val analysis = CloudSync.analyzePush(this@LauncherActivity, c)
        if (analysis.all.isEmpty() && analysis.toDelete.isEmpty()) {
            LauncherLog.log("Keep local: nothing local to push")
            return
        }
        CloudSync.pushItems(c, analysis.all, toDelete = analysis.toDelete).collect { }
    }

    /** One cloud job at a time: overlapping logins on one account trip Steam's guard. */
    private fun runCloudJob(block: suspend () -> Unit) {
        if (cloudJob?.isActive == true || downloadJob?.isActive == true) {
            LauncherLog.log("Another Steam operation is running — skipping")
            return
        }
        spinCloud.visibility = View.VISIBLE
        cloudJob = uiScope.launch {
            try {
                block()
            } finally {
                spinCloud.visibility = View.GONE
                cloudJob = null
                refreshUi()
            }
        }
        refreshUi()
    }

    // ── Launch ─────────────────────────────────────────────────────────

    private fun onLaunchClicked() {
        val c = creds
        if (c != null && settings.autoPull) {
            runCloudJob {
                if (pullFlow(c, source = "pre-launch")) launchGame()
            }
        } else {
            launchGame()
        }
    }

    private fun launchGame() {
        btnLaunch.isEnabled = false
        txtStatus.text = getString(R.string.launcher_status_preparing, "Preparing…")
        uiScope.launch {
            try {
                withContext(Dispatchers.IO) {
                    GamePrep.prepare(this@LauncherActivity, force = false) { step ->
                        runOnUiThread { txtStatus.text = getString(R.string.launcher_status_preparing, step) }
                    }
                }
                LauncherLog.log("Starting the game")
                returningFromGame = true
                startActivity(Intent(this@LauncherActivity, GodotApp::class.java))
            } catch (t: Throwable) {
                LauncherLog.log("Could not prepare the game: ${t.message ?: t.javaClass.simpleName}", t)
                txtStatus.text = getString(R.string.launcher_status_error, t.message ?: t.javaClass.simpleName)
            } finally {
                refreshUi()
            }
        }
    }
}
