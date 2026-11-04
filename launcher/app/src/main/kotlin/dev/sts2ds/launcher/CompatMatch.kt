package dev.sts2ds.launcher

import org.json.JSONArray
import org.json.JSONObject

/**
 * Which target of a full compat pack fits the installed game: one listing
 * sts2.dll's SHA-256 first, else one listing its version. A full pack may
 * not claim every version or build ("*"); only the offline pack, chosen
 * apart from this, may. As Sts2MobileLauncher's CompatPackManager.
 */
object CompatMatch {
    private const val WILDCARD = "*"

    fun target(manifest: JSONObject, dllSha: String, version: String): JSONObject? {
        val targets = manifest.optJSONArray("targets") ?: return null
        var byVersion: JSONObject? = null
        for (i in 0 until targets.length()) {
            val target = targets.getJSONObject(i)
            val shas = strings(target.opt("sts2_dll_sha256"))
            val versions = strings(target.opt("versions"))
            if (WILDCARD in shas || WILDCARD in versions) {
                continue
            }

            if (shas.any { it.equals(dllSha, ignoreCase = true) }) {
                return target
            }
            if (byVersion == null && version.isNotEmpty() && version in versions) {
                byVersion = target
            }
        }
        return byVersion
    }

    /** A manifest field that is one string or an array of them. */
    private fun strings(value: Any?): List<String> = when (value) {
        is String -> listOf(value)
        is JSONArray -> (0 until value.length()).map { value.getString(it) }
        else -> emptyList()
    }
}
