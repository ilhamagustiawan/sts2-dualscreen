// LauncherActivity — the app's entry point: sign in, download, launch.
//
// Modelled on SilksongAndroid's LauncherActivity and SetupActivity (MIT,
// https://github.com/jakobkhansen/SilksongAndroid). The page is a guided one:
// a checklist of the three steps of a fresh install, one primary button that
// always does the next one, and the Steam Cloud status beside it. There is
// nothing to compile for STS2, so "set up" is just the depot download (run by
// DownloadService so it survives the screen going off), and launching runs
// GamePrep and starts the Godot activity.
//
// Cloud saves go through SaveSync: a pull before each launch and a push after
// it, both automatic by default. The push is owed from the moment the game
// starts (SettingsStore.pendingPush), not remembered here, because Android may
// well end this process while the game has the memory.
//
// This activity runs in its own ":launcher" process, as Silksong's does:
// Godot ends its process when the game quits, and the launcher should still
// be there afterwards to upload saves.

package dev.sts2ds.launcher

import android.Manifest
import android.app.Activity
import android.app.ActivityManager
import android.app.AlertDialog
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.text.format.DateUtils
import android.widget.Toast
import android.view.View
import android.widget.Button
import android.widget.ProgressBar
import android.widget.TextView
import com.godot.game.GodotApp
import dev.sts2ds.R
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext

class LauncherActivity : Activity() {

    companion object {
        /** Set by GodotApp when it bounced back here because the game was not ready. */
        const val EXTRA_ERROR = "launch_error"
        private const val REQ_LOGIN = 1001
        private const val REQ_NOTIFICATIONS = 1002

        /** How long to wait for the game's process to go before deciding it is still running. */
        private const val GAME_EXIT_WAIT_MS = 5000L
        private const val GAME_EXIT_POLL_MS = 250L
    }

    /** The one thing the primary button does next. */
    private enum class Stage { SIGN_IN, DOWNLOAD, DOWNLOADING, READY }

    private val uiScope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private lateinit var tokenStore: TokenStore
    private lateinit var settings: SettingsStore
    private lateinit var sync: SaveSync
    private var creds: TokenStore.Credentials? = null
    private var cloudJob: Job? = null
    private var launching = false
    private var waitingForGame = false
    private var gameStillRunning = false

    /** A result worth showing until the next action (download finished, failed…). */
    private var notice: String? = null

    /** Focus moves to the primary button only when what it does changes. */
    private var shownStage: Stage? = null

    private lateinit var txtStatus: TextView
    private lateinit var btnPrimary: Button
    private lateinit var progressDownload: ProgressBar
    private lateinit var txtProgress: TextView
    private lateinit var btnUpdate: Button
    private lateinit var stepAccount: View
    private lateinit var stepGame: View
    private lateinit var stepPlay: View
    private lateinit var txtCloudStatus: TextView
    private lateinit var rowCloudButtons: View
    private lateinit var btnSync: Button
    private lateinit var btnPull: Button
    private lateinit var btnPush: Button
    private lateinit var spinCloud: ProgressBar
    private lateinit var txtLogTail: TextView

    private val logListener = LauncherLog.Listener { line, _ ->
        runOnUiThread { txtLogTail.text = line }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_launcher)
        tokenStore = TokenStore(this)
        settings = SettingsStore(this)
        sync = SaveSync(this, settings, ::confirmConflictResolution)
        creds = tokenStore.read()

        txtStatus = findViewById(R.id.txt_status)
        btnPrimary = findViewById(R.id.btn_primary)
        progressDownload = findViewById(R.id.progress_download)
        txtProgress = findViewById(R.id.txt_progress)
        btnUpdate = findViewById(R.id.btn_update)
        stepAccount = findViewById(R.id.step_account)
        stepGame = findViewById(R.id.step_game)
        stepPlay = findViewById(R.id.step_play)
        txtCloudStatus = findViewById(R.id.txt_cloud_status)
        btnSync = findViewById(R.id.btn_sync)
        rowCloudButtons = btnSync.parent as View
        btnPull = findViewById(R.id.btn_pull)
        btnPush = findViewById(R.id.btn_push)
        spinCloud = findViewById(R.id.spin_cloud)
        txtLogTail = findViewById(R.id.txt_log_tail)

        stepAccount.isFocusable = true
        stepAccount.isClickable = true
        stepAccount.setOnClickListener { onAccountClicked() }

        btnPrimary.setOnClickListener { onPrimaryClicked() }
        btnUpdate.setOnClickListener { startDownload() }
        btnSync.setOnClickListener { creds?.let { c -> runCloudJob { handle(sync.syncNow(c)) } } }
        btnPull.setOnClickListener { creds?.let { c -> runCloudJob { handle(sync.pull(c)) } } }
        btnPush.setOnClickListener { creds?.let { c -> runCloudJob { handle(sync.push(c)) } } }
        findViewById<Button>(R.id.btn_settings).setOnClickListener {
            startActivity(Intent(this, SettingsActivity::class.java))
        }
        val openLogs = View.OnClickListener { startActivity(Intent(this, LogActivity::class.java)) }
        findViewById<Button>(R.id.btn_logs).setOnClickListener(openLogs)
        txtLogTail.setOnClickListener(openLogs)

        LauncherLog.addListener(logListener)
        txtLogTail.text = LauncherLog.snapshot().lastOrNull().orEmpty()
        showLaunchError(intent)

        uiScope.launch { DownloadState.flow.collect { onDownloadState(it) } }
        refreshUi()
    }

    // singleTask: GodotApp bouncing back lands here, not in onCreate.
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        showLaunchError(intent)
        refreshUi()
    }

    private fun showLaunchError(intent: Intent) {
        intent.getStringExtra(EXTRA_ERROR)?.let {
            LauncherLog.log("Game did not start: $it")
            notice = getString(R.string.launcher_status_error, it)
            intent.removeExtra(EXTRA_ERROR)
        }
    }

    override fun onDestroy() {
        LauncherLog.removeListener(logListener)
        uiScope.cancel()
        super.onDestroy()
    }

    override fun onResume() {
        super.onResume()
        if (settings.animWarmupCrashed) {
            settings.animWarmupCrashed = false
            Toast.makeText(this, R.string.settings_anim_warmup_crashed_notice, Toast.LENGTH_LONG).show()
        }
        refreshUi()
        // Owed since the last game started; also retries one that failed or
        // was put off because the game was still running.
        maybeAutoPush()
    }

    // ── State ──────────────────────────────────────────────────────────

    private fun stage(): Stage = when {
        DownloadState.isActive -> Stage.DOWNLOADING
        // Another branch chosen in settings: download it before playing.
        DepotLocation.present(this) && !DepotLocation.branchMismatch(this) -> Stage.READY
        creds == null -> Stage.SIGN_IN
        else -> Stage.DOWNLOAD
    }

    private fun refreshUi() {
        val c = creds
        val stage = stage()
        val hasGame = DepotLocation.present(this)
        val switchTo = switchTarget()
        val cloudBusy = cloudJob?.isActive == true
        val busy = cloudBusy || launching

        btnPrimary.text = when (stage) {
            Stage.SIGN_IN -> getString(R.string.action_log_in)
            Stage.DOWNLOAD -> when {
                switchTo != null -> getString(R.string.action_switch_branch, switchTo)
                DepotLocation.interrupted(this) -> getString(R.string.action_resume_download)
                else -> getString(R.string.action_download)
            }
            Stage.DOWNLOADING -> getString(R.string.action_cancel_download)
            Stage.READY -> getString(R.string.action_launch)
        }
        btnPrimary.isEnabled = !busy
        btnPrimary.alpha = if (btnPrimary.isEnabled) 1f else 0.4f

        btnUpdate.visibility = if (hasGame && c != null && stage != Stage.DOWNLOADING) View.VISIBLE else View.GONE
        btnUpdate.isEnabled = !busy

        val dl = DownloadState.flow.value
        progressDownload.visibility = if (stage == Stage.DOWNLOADING) View.VISIBLE else View.GONE
        txtProgress.visibility = progressDownload.visibility

        txtStatus.text = when {
            // GamePrep's steps are written straight in by launchGame.
            launching -> txtStatus.text
            cloudBusy -> getString(R.string.launcher_status_syncing)
            waitingForGame -> getString(R.string.launcher_status_waiting_game)
            notice != null -> notice
            switchTo != null -> getString(R.string.launcher_status_switch_branch, switchTo)
            hasGame -> installedVersion().let {
                if (it.isEmpty()) getString(R.string.launcher_status_ready_unknown)
                else getString(R.string.launcher_status_ready, it)
            }
            c == null -> getString(R.string.launcher_status_need_login)
            DepotLocation.interrupted(this) -> getString(R.string.launcher_status_interrupted)
            else -> getString(R.string.launcher_status_need_download, c.accountName)
        }

        showSteps(stage)
        showDownloadProgress(dl)
        showCloud(c, cloudBusy)

        if (stage != shownStage && !busy) btnPrimary.requestFocus()
        shownStage = stage
    }

    /**
     * Just the views that move while a download runs. Called for every
     * progress update, so it stays clear of the disk checks refreshUi makes.
     */
    private fun showDownloadProgress(dl: DownloadState.State) {
        val detail = stepGame.findViewById<TextView>(R.id.step_detail)
        when (dl) {
            is DownloadState.State.Running -> {
                val text = "%.1f%% · %s".format(dl.fraction * 100, formatBytes(dl.bytes))
                progressDownload.isIndeterminate = false
                progressDownload.progress = (dl.fraction * 1000).toInt()
                txtProgress.text = dl.status ?: ""
                txtStatus.text = getString(R.string.launcher_status_downloading, text)
                detail.text = getString(R.string.step_game_running, "%.0f%%".format(dl.fraction * 100))
            }
            is DownloadState.State.Preparing -> {
                progressDownload.isIndeterminate = true
                txtProgress.text = dl.step
                txtStatus.text = getString(R.string.launcher_status_preparing, dl.step)
                detail.text = getString(R.string.step_game_preparing)
            }
            else -> Unit
        }
    }

    private fun showSteps(stage: Stage) {
        val c = creds
        showStep(
            stepAccount, 1, getString(R.string.step_account_title),
            if (c != null) getString(R.string.step_account_done, c.accountName) else getString(R.string.step_account_todo),
            done = c != null, current = stage == Stage.SIGN_IN,
        )
        val hasGame = DepotLocation.present(this)
        val switchTo = switchTarget()
        showStep(
            stepGame, 2, getString(R.string.step_game_title),
            when {
                hasGame && switchTo != null -> getString(R.string.step_game_switch, switchTo)
                hasGame -> installedVersion().let {
                    if (it.isEmpty()) getString(R.string.step_game_done_unknown) else getString(R.string.step_game_done, it)
                }
                DepotLocation.interrupted(this) -> getString(R.string.step_game_interrupted)
                else -> getString(R.string.step_game_todo)
            },
            done = hasGame && switchTo == null && stage != Stage.DOWNLOADING,
            current = stage == Stage.DOWNLOAD || stage == Stage.DOWNLOADING,
        )
        showStep(
            stepPlay, 3, getString(R.string.step_play_title),
            getString(if (stage == Stage.READY) R.string.step_play_done else R.string.step_play_todo),
            done = false, current = stage == Stage.READY,
        )
    }

    /** "Beta" or "Stable" when settings name another branch than the installed one. */
    private fun switchTarget(): String? =
        if (DepotLocation.branchMismatch(this)) SettingsStore.branchName(this, settings.gameBranch) else null

    /** The installed version, marked as the beta when it is one. */
    private fun installedVersion(): String {
        val version = GamePrep.version(this)
        if (version.isEmpty() || DepotLocation.installedBranch(this) != SettingsStore.BRANCH_BETA) return version
        return "$version ${getString(R.string.branch_beta)}"
    }

    private fun showStep(row: View, number: Int, title: String, detail: String, done: Boolean, current: Boolean) {
        val badge: TextView = row.findViewById(R.id.step_badge)
        badge.text = if (done) "✓" else number.toString()
        // The current badge is gold, so its number goes dark.
        badge.setTextColor(getColor(if (current && !done) R.color.text_on_control else R.color.text_primary))
        badge.setBackgroundResource(
            when {
                done -> R.drawable.bg_step_done
                current -> R.drawable.bg_step_current
                else -> R.drawable.bg_step_todo
            },
        )
        row.findViewById<TextView>(R.id.step_title).apply {
            text = title
            setTextColor(getColor(if (done || current) R.color.text_primary else R.color.text_dim))
        }
        row.findViewById<TextView>(R.id.step_detail).text = detail
    }

    private fun showCloud(c: TokenStore.Credentials?, cloudBusy: Boolean) {
        if (c == null) {
            txtCloudStatus.text = getString(R.string.cloud_signed_out)
            rowCloudButtons.visibility = View.GONE
            spinCloud.visibility = View.GONE
            return
        }
        rowCloudButtons.visibility = View.VISIBLE
        spinCloud.visibility = if (cloudBusy) View.VISIBLE else View.GONE
        val enabled = !cloudBusy && !launching && !DownloadState.isActive
        btnSync.isEnabled = enabled
        btnPull.isEnabled = enabled
        btnPush.isEnabled = enabled

        val auto = getString(
            when {
                settings.autoPull && settings.autoPush -> R.string.cloud_auto_on
                settings.autoPull -> R.string.cloud_auto_pull_only
                settings.autoPush -> R.string.cloud_auto_push_only
                else -> R.string.cloud_auto_off
            },
        )
        val error = settings.lastSyncError
        val state = when {
            cloudBusy -> getString(R.string.cloud_syncing)
            gameStillRunning || waitingForGame -> getString(R.string.launcher_status_waiting_game)
            error == SaveSync.OFFLINE -> getString(R.string.cloud_failed, getString(R.string.cloud_offline))
            error == SaveSync.AUTH_EXPIRED -> getString(R.string.cloud_failed, getString(R.string.cloud_auth))
            error != null -> getString(R.string.cloud_failed, error)
            settings.pendingPush -> getString(R.string.cloud_pending)
            settings.lastSyncAt > 0 -> getString(
                R.string.cloud_synced,
                DateUtils.getRelativeTimeSpanString(
                    settings.lastSyncAt, System.currentTimeMillis(), DateUtils.MINUTE_IN_MILLIS,
                ).toString().lowercase(),
            )
            else -> getString(R.string.cloud_never)
        }
        txtCloudStatus.text = getString(R.string.cloud_status, auto, state)
    }

    // ── Primary action ─────────────────────────────────────────────────

    private fun onPrimaryClicked() {
        notice = null
        when (stage()) {
            Stage.SIGN_IN -> startLogin()
            Stage.DOWNLOAD -> startDownload()
            Stage.DOWNLOADING -> DownloadService.cancel(this)
            Stage.READY -> onLaunchClicked()
        }
    }

    // ── Login ──────────────────────────────────────────────────────────

    private fun onAccountClicked() {
        if (creds == null) {
            startLogin()
            return
        }
        AlertDialog.Builder(this)
            .setMessage(R.string.logout_message)
            .setNegativeButton(android.R.string.cancel, null)
            .setPositiveButton(R.string.logout_confirm) { _, _ ->
                LauncherLog.log("Logged out")
                tokenStore.clear()
                creds = null
                refreshUi()
            }
            .show()
    }

    private fun startLogin() {
        @Suppress("DEPRECATION")
        startActivityForResult(Intent(this, LoginActivity::class.java), REQ_LOGIN)
    }

    /**
     * Steam turned the saved token down. Offers a fresh sign-in; [orElse]
     * runs if the user declines (a launch carries on without syncing).
     */
    private fun promptReauth(orElse: (() -> Unit)? = null) {
        val account = creds?.accountName.orEmpty()
        AlertDialog.Builder(this)
            .setTitle(R.string.auth_expired_title)
            .setMessage(getString(R.string.auth_expired_message, account))
            .setPositiveButton(R.string.auth_sign_in) { _, _ ->
                LauncherLog.log("Saved Steam sign-in rejected; signing in again")
                tokenStore.clear()
                creds = null
                settings.lastSyncError = null
                refreshUi()
                startLogin()
            }
            .setNegativeButton(android.R.string.cancel) { _, _ -> orElse?.invoke() }
            .setOnCancelListener { orElse?.invoke() }
            .show()
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

    private fun startDownload() {
        notice = null
        if (creds == null || DownloadState.isActive) return
        if (cloudJob?.isActive == true) {
            LauncherLog.log("Another Steam operation is running — skipping")
            return
        }
        // So the progress notification can show; the download runs either way.
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), REQ_NOTIFICATIONS)
        }
        DownloadService.start(this)
        refreshUi()
    }

    private fun onDownloadState(state: DownloadState.State) {
        when (state) {
            DownloadState.State.Done -> {
                notice = getString(R.string.launcher_status_download_done)
                DownloadState.acknowledge()
            }
            DownloadState.State.Cancelled -> {
                notice = getString(R.string.launcher_status_cancelled)
                DownloadState.acknowledge()
            }
            is DownloadState.State.Failed -> {
                notice = getString(R.string.launcher_status_download_failed, state.message)
                DownloadState.acknowledge()
                if (state.authExpired) promptReauth()
            }
            is DownloadState.State.Running, is DownloadState.State.Preparing -> {
                // A full refresh only when the stage flips; otherwise just the bar.
                if (shownStage == Stage.DOWNLOADING) return showDownloadProgress(state)
            }
            DownloadState.State.Idle -> Unit
        }
        refreshUi()
    }

    // ── Cloud saves ────────────────────────────────────────────────────

    /** What the UI owes the user after a sync: the status line covers the rest. */
    private fun handle(outcome: SaveSync.Outcome) {
        if (outcome == SaveSync.Outcome.AUTH_EXPIRED) promptReauth()
    }

    /**
     * After a game session, when Settings → auto-push is on. Waits for the
     * game's process to go first: uploading while the game can still write
     * would send a half-saved set.
     */
    private fun maybeAutoPush() {
        val c = creds ?: return
        if (!settings.pendingPush || !settings.autoPush) return
        if (waitingForGame || launching || cloudJob?.isActive == true || DownloadState.isActive) return
        waitingForGame = true
        refreshUi()
        uiScope.launch {
            val exited = waitForGameExit()
            waitingForGame = false
            gameStillRunning = !exited
            if (!exited) {
                LauncherLog.log("Auto-push: the game is still running; uploading once it closes")
                refreshUi()
                return@launch
            }
            LauncherLog.log("Auto-push: starting (returned from game)")
            runCloudJob { handle(sync.push(c)) }
        }
    }

    private suspend fun waitForGameExit(): Boolean {
        var waited = 0L
        while (isGameProcessAlive()) {
            if (waited >= GAME_EXIT_WAIT_MS) return false
            delay(GAME_EXIT_POLL_MS)
            waited += GAME_EXIT_POLL_MS
        }
        return true
    }

    /** The game runs in the app's main process, named after the package; the launcher is ":launcher". */
    private fun isGameProcessAlive(): Boolean =
        getSystemService(ActivityManager::class.java)?.runningAppProcesses
            ?.any { it.processName == packageName } == true

    /**
     * Conflict resolver, shown only when both this device and the cloud have
     * newer saves (see SyncPlan). The winning side replaces the other for the
     * whole save set, never a per-slot mix. Before a launch the neutral button
     * plays without syncing; otherwise it cancels.
     */
    private suspend fun confirmConflictResolution(conflict: SaveSync.Conflict): SaveSync.Choice =
        withContext(Dispatchers.Main) {
            suspendCancellableCoroutine { cont ->
                fun finish(choice: SaveSync.Choice) {
                    if (cont.isActive) cont.resumeWith(Result.success(choice))
                }
                val files = buildString {
                    for (name in conflict.files.take(8)) append("• ").append(name).append('\n')
                    if (conflict.files.size > 8) {
                        append(getString(R.string.conflict_more, conflict.files.size - 8)).append('\n')
                    }
                }
                val dialog = AlertDialog.Builder(this@LauncherActivity)
                    .setTitle(R.string.conflict_title)
                    .setMessage(
                        getString(
                            R.string.conflict_message,
                            formatSaveTime(conflict.deviceNewest),
                            formatSaveTime(conflict.cloudNewest),
                            files,
                        ),
                    )
                    .setPositiveButton(R.string.conflict_keep_device) { _, _ -> finish(SaveSync.Choice.KEEP_DEVICE) }
                    .setNegativeButton(R.string.conflict_keep_cloud) { _, _ -> finish(SaveSync.Choice.KEEP_CLOUD) }
                    .apply {
                        if (conflict.beforeLaunch) {
                            setNeutralButton(R.string.conflict_play_anyway) { _, _ -> finish(SaveSync.Choice.SKIP) }
                        } else {
                            setNeutralButton(android.R.string.cancel) { _, _ -> finish(SaveSync.Choice.CANCEL) }
                        }
                    }
                    .setOnCancelListener { finish(SaveSync.Choice.CANCEL) }
                    .show()
                cont.invokeOnCancellation { dialog.dismiss() }
            }
        }

    /** "Today 14:02 (3 hours ago)" from unix seconds. */
    private fun formatSaveTime(unix: Long): String {
        if (unix <= 0) return getString(R.string.conflict_unknown_time)
        val millis = unix * 1000L
        val absolute = DateUtils.formatDateTime(
            this, millis,
            DateUtils.FORMAT_SHOW_DATE or DateUtils.FORMAT_SHOW_TIME or DateUtils.FORMAT_ABBREV_MONTH,
        )
        val relative = DateUtils.getRelativeTimeSpanString(millis, System.currentTimeMillis(), DateUtils.MINUTE_IN_MILLIS)
        return "$absolute ($relative)"
    }

    /** One cloud job at a time: overlapping logins on one account trip Steam's guard. */
    private fun runCloudJob(block: suspend () -> Unit) {
        if (cloudJob?.isActive == true || DownloadState.isActive) {
            LauncherLog.log("Another Steam operation is running — skipping")
            return
        }
        notice = null
        gameStillRunning = false
        cloudJob = uiScope.launch {
            try {
                block()
            } finally {
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
                when (sync.pull(c, beforeLaunch = true)) {
                    SaveSync.Outcome.CANCELLED -> Unit
                    SaveSync.Outcome.AUTH_EXPIRED -> promptReauth(orElse = ::launchGame)
                    else -> launchGame()
                }
            }
        } else {
            launchGame()
        }
    }

    private fun launchGame() {
        launching = true
        txtStatus.text = getString(R.string.launcher_status_preparing, "Preparing…")
        refreshUi()
        uiScope.launch {
            try {
                withContext(Dispatchers.IO) {
                    GamePrep.prepare(this@LauncherActivity, force = false) { step ->
                        runOnUiThread { txtStatus.text = getString(R.string.launcher_status_preparing, step) }
                    }
                }
                LauncherLog.log("Starting the game")
                // Owed from now on, and on disk so it outlives this process.
                if (creds != null) settings.pendingPush = true
                startActivity(Intent(this@LauncherActivity, GodotApp::class.java))
            } catch (t: Throwable) {
                LauncherLog.log("Could not prepare the game: ${t.message ?: t.javaClass.simpleName}", t)
                notice = getString(R.string.launcher_status_error, t.message ?: t.javaClass.simpleName)
            } finally {
                launching = false
                refreshUi()
            }
        }
    }
}
