// OfflineGuard — the generic offline compat pack runs only with the user's say.
//
// When no target of the full compat pack fits the installed game, GamePrep
// falls back to the offline pack: reflection patches that may or may not
// hold for that build. The pack reports how far it got in
// files/launcher/offline-bootstrap-probe.json. This file remembers that
// verdict per tuple (pack, target, pack version, game version, sts2.dll) and
// decides what the launcher asks before the next launch:
//
//   no verdict yet ──▶ confirm once ──▶ launch ──▶ probe ──▶ verdict
//   ready          ──▶ launch
//   failed         ──▶ show the failure, every launch; retry is the user's call
//
// The probe cannot name the tuple itself (it reads a payload manifest we
// don't write), so the launcher marks the tuple pending before the launch and
// collects the probe after the game's process has gone.
//
// Reimplemented from Sts2MobileLauncher's compat-pack-loading-flow.md (MIT).

package dev.sts2ds.launcher

import android.content.Context
import org.json.JSONException
import org.json.JSONObject
import java.io.File

object OfflineGuard {
    private const val PREFS = "offline_guard"
    private const val PENDING_KEY = "pending_tuple"
    private const val PROBE_FILE = "launcher/offline-bootstrap-probe.json"

    // Probe statuses (offline-bootstrap-v2).
    private const val STATUS_READY = "ready"
    private val FAILED_STATUSES = setOf("unsupported_api", "apply_failed", "runtime_failed")

    sealed class Verdict {
        data object Ready : Verdict()
        data object Unverified : Verdict()
        data class Failed(val status: String, val summary: String) : Verdict()
    }

    enum class Prompt { NONE, CONFIRM, FAILED }

    enum class Confirmation { NOT_YET, GIVEN }

    fun tuple(packId: String, targetId: String, compatVersion: String, gameVersion: String, dllSha: String) =
        "$packId/$targetId/$compatVersion/$gameVersion/$dllSha"

    /** What a probe file says; anything unreadable or unfinished proves nothing. */
    fun verdict(probeJson: String?): Verdict {
        val json = parse(probeJson) ?: return Verdict.Unverified
        return verdictOf(text(json, "Status"), text(json, "FailureSummary"))
    }

    fun prompt(verdict: Verdict, confirmation: Confirmation): Prompt = when (verdict) {
        Verdict.Ready -> Prompt.NONE
        is Verdict.Failed -> Prompt.FAILED
        Verdict.Unverified -> if (confirmation == Confirmation.GIVEN) Prompt.NONE else Prompt.CONFIRM
    }

    private fun verdictOf(status: String, summary: String): Verdict = when (status) {
        STATUS_READY -> Verdict.Ready
        in FAILED_STATUSES -> Verdict.Failed(status, summary)
        else -> Verdict.Unverified
    }

    // ── Stored per tuple ───────────────────────────────────────────────

    /** The verdict of the last run of [tuple]. */
    fun stored(context: Context, tuple: String): Verdict {
        val prefs = prefs(context)
        return verdictOf(prefs.getString("status:$tuple", "").orEmpty(), prefs.getString("summary:$tuple", "").orEmpty())
    }

    /** What the launcher should ask before launching [tuple]. */
    fun promptFor(context: Context, tuple: String): Prompt {
        val confirmed = prefs(context).getBoolean("confirmed:$tuple", false)
        return prompt(stored(context, tuple), if (confirmed) Confirmation.GIVEN else Confirmation.NOT_YET)
    }

    /** The user agreed to launch [tuple]; drops the old probe so the next one is this run's. */
    fun beforeLaunch(context: Context, tuple: String) {
        probeFile(context).delete()
        prefs(context).edit()
            .putBoolean("confirmed:$tuple", true)
            .putString(PENDING_KEY, tuple)
            .commit()
    }

    fun hasPending(context: Context): Boolean = prefs(context).contains(PENDING_KEY)

    /** After the game's process has gone: records the run's verdict for its tuple. */
    fun collect(context: Context): Verdict? {
        val prefs = prefs(context)
        val tuple = prefs.getString(PENDING_KEY, null) ?: return null
        val file = probeFile(context)
        val json = parse(if (file.isFile) file.readText() else null)
        val status = json?.let { text(it, "Status") }.orEmpty()
        val summary = json?.let { text(it, "FailureSummary") }.orEmpty()

        prefs.edit()
            .putString("status:$tuple", status)
            .putString("summary:$tuple", summary)
            .remove(PENDING_KEY)
            .commit()
        return verdictOf(status, summary)
    }

    private fun parse(text: String?): JSONObject? {
        if (text == null) {
            return null
        }
        return try {
            JSONObject(text)
        } catch (_: JSONException) {
            null
        }
    }

    /** The probe writes null for an unset field; optString would make that "null". */
    private fun text(json: JSONObject, key: String): String =
        if (json.isNull(key)) "" else json.optString(key)

    private fun probeFile(context: Context) = File(context.filesDir, PROBE_FILE)

    private fun prefs(context: Context) = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
}
