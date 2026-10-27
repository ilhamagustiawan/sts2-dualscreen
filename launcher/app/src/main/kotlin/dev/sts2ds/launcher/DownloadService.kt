// DownloadService — the game download, kept alive while the launcher is not.
//
// The depot is several gigabytes. Run from the activity, it stopped whenever
// the screen went off or the user switched apps, and Android could take the
// process with it. A foreground service with a progress notification is the
// platform's answer to "keep doing this while I look away"; the wake and Wi-Fi
// locks keep the CPU and radio up while the screen sleeps.
//
// It runs in ":launcher", next to the activity, and reports through
// DownloadState. The download is resumable, so cancelling (from the launcher
// or the notification) loses nothing.

package dev.sts2ds.launcher

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.wifi.WifiManager
import android.os.IBinder
import android.os.PowerManager
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import dev.sts2ds.R
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

class DownloadService : Service() {

    companion object {
        private const val ACTION_START = "dev.sts2ds.download.START"
        private const val ACTION_CANCEL = "dev.sts2ds.download.CANCEL"
        private const val CHANNEL = "download"
        private const val ID_PROGRESS = 1
        private const val ID_RESULT = 2

        /** Repaints of the notification, at most this often; every chunk is far too often. */
        private const val NOTIFY_INTERVAL_MS = 1000L

        fun start(context: Context) {
            // Before the service is up, so the launcher shows the download at once.
            DownloadState.set(DownloadState.State.Running(0f, 0L, null))
            context.startForegroundService(Intent(context, DownloadService::class.java).setAction(ACTION_START))
        }

        fun cancel(context: Context) {
            context.startService(Intent(context, DownloadService::class.java).setAction(ACTION_CANCEL))
        }
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private var job: Job? = null
    private var wakeLock: PowerManager.WakeLock? = null
    private var wifiLock: WifiManager.WifiLock? = null
    private var lastNotify = 0L

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_CANCEL -> {
                if (job?.isActive == true) {
                    LauncherLog.log("Download cancelled; it resumes where it stopped next time")
                    job?.cancel()
                } else {
                    stopSelf()
                }
            }
            else -> begin()
        }
        return START_NOT_STICKY
    }

    private fun begin() {
        createChannel()
        ServiceCompat.startForeground(
            this, ID_PROGRESS, progressNotification(getString(R.string.download_notif_starting), -1f),
            ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC,
        )
        if (job?.isActive == true) return
        val creds = TokenStore(this).read()
        if (creds == null) {
            DownloadState.set(DownloadState.State.Failed(getString(R.string.download_failed_signed_out), authExpired = true))
            finish()
            return
        }
        acquireLocks()
        getSystemService(NotificationManager::class.java).cancel(ID_RESULT)
        DownloadState.set(DownloadState.State.Running(0f, 0L, null))

        val target = DepotLocation.gameDir(this)
        job = scope.launch {
            try {
                var status: String? = null
                DepotFetcher.download(creds, target, DepotLocation.stagingDir(this@DownloadService)).collect { event ->
                    when (event) {
                        is DepotFetcher.Event.Progress -> {
                            val fraction = event.fraction.coerceIn(0f, 1f)
                            DownloadState.set(DownloadState.State.Running(fraction, event.bytes, status))
                            updateProgress(fraction, event.bytes)
                        }
                        is DepotFetcher.Event.Status -> {
                            status = event.message
                            val cur = DownloadState.flow.value as? DownloadState.State.Running
                            DownloadState.set(DownloadState.State.Running(cur?.fraction ?: 0f, cur?.bytes ?: 0L, status))
                        }
                        DepotFetcher.Event.Done -> LauncherLog.log("Game downloaded to $target")
                    }
                }
                // Stage once now so the first launch is quick and any problem
                // with the files shows up here rather than as a failed launch.
                withContext(Dispatchers.IO) {
                    GamePrep.prepare(this@DownloadService, force = true) { step ->
                        DownloadState.set(DownloadState.State.Preparing(step))
                    }
                }
                DownloadState.set(DownloadState.State.Done)
                notifyResult(getString(R.string.download_notif_done_title), getString(R.string.download_notif_done_text))
            } catch (_: CancellationException) {
                DownloadState.set(DownloadState.State.Cancelled)
            } catch (t: Throwable) {
                val msg = t.message ?: t.javaClass.simpleName
                LauncherLog.log("Download failed: $msg", t)
                DownloadState.set(DownloadState.State.Failed(msg, t.isSteamAuthExpired()))
                notifyResult(getString(R.string.download_notif_failed_title), msg)
            } finally {
                finish()
            }
        }
    }

    private fun finish() {
        releaseLocks()
        ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    override fun onDestroy() {
        releaseLocks()
        scope.cancel()
        // Swiped away or killed mid-run: the files on disk say "interrupted".
        if (DownloadState.isActive) DownloadState.set(DownloadState.State.Cancelled)
        super.onDestroy()
    }

    // ── Locks ──────────────────────────────────────────────────────────

    private fun acquireLocks() {
        wakeLock = getSystemService(PowerManager::class.java)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "sts2ds:download")
            .apply { setReferenceCounted(false); acquire() }
        @Suppress("DEPRECATION")
        wifiLock = applicationContext.getSystemService(WifiManager::class.java)
            ?.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "sts2ds:download")
            ?.apply { setReferenceCounted(false); acquire() }
    }

    private fun releaseLocks() {
        wakeLock?.takeIf { it.isHeld }?.release()
        wifiLock?.takeIf { it.isHeld }?.release()
        wakeLock = null
        wifiLock = null
    }

    // ── Notifications ──────────────────────────────────────────────────

    private fun createChannel() {
        val nm = getSystemService(NotificationManager::class.java)
        if (nm.getNotificationChannel(CHANNEL) != null) return
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL, getString(R.string.download_channel), NotificationManager.IMPORTANCE_LOW),
        )
    }

    private fun openLauncher(): PendingIntent = PendingIntent.getActivity(
        this, 0,
        Intent(this, LauncherActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
    )

    /** [fraction] below 0 shows an indeterminate bar. */
    private fun progressNotification(text: String, fraction: Float) =
        NotificationCompat.Builder(this, CHANNEL)
            .setSmallIcon(android.R.drawable.stat_sys_download)
            .setContentTitle(getString(R.string.download_notif_title))
            .setContentText(text)
            .setOnlyAlertOnce(true)
            .setOngoing(true)
            .setProgress(1000, (fraction.coerceAtLeast(0f) * 1000).toInt(), fraction < 0f)
            .setContentIntent(openLauncher())
            .addAction(
                0, getString(R.string.action_cancel),
                PendingIntent.getService(
                    this, 1,
                    Intent(this, DownloadService::class.java).setAction(ACTION_CANCEL),
                    PendingIntent.FLAG_IMMUTABLE,
                ),
            )
            .build()

    private fun updateProgress(fraction: Float, bytes: Long) {
        val now = System.currentTimeMillis()
        if (now - lastNotify < NOTIFY_INTERVAL_MS) return
        lastNotify = now
        val text = "%.1f%% · %s".format(fraction * 100, formatBytes(bytes))
        getSystemService(NotificationManager::class.java).notify(ID_PROGRESS, progressNotification(text, fraction))
    }

    private fun notifyResult(title: String, text: String) {
        getSystemService(NotificationManager::class.java).notify(
            ID_RESULT,
            NotificationCompat.Builder(this, CHANNEL)
                .setSmallIcon(android.R.drawable.stat_sys_download_done)
                .setContentTitle(title)
                .setContentText(text)
                .setAutoCancel(true)
                .setContentIntent(openLauncher())
                .build(),
        )
    }
}

fun formatBytes(bytes: Long): String = when {
    bytes >= 1L shl 30 -> "%.2f GB".format(bytes / (1L shl 30).toDouble())
    bytes >= 1L shl 20 -> "%.0f MB".format(bytes / (1L shl 20).toDouble())
    else -> "%d KB".format(bytes / 1024)
}
