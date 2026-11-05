// GameIcon — the game's own icon, as Steam serves it to the account that owns it.
//
// Nothing game-derived ships in the APK (see DepotFetcher), so this is fetched
// at runtime: PICS names the client icon by its SHA-1, Steam's CDN serves the
// .ico, and BitmapFactory decodes its largest image.

package dev.sts2ds.launcher

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import java.net.HttpURLConnection
import java.net.URL

object GameIcon {

    /** The icon, and the game's name as Steam has it (null if Steam sent none). */
    class Art(val name: String?, val bitmap: Bitmap)

    private const val CDN = "https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps"
    private const val TIMEOUT_MS = 15_000
    private val SHA1_HEX = Regex("[0-9a-f]{40}")

    /** Blocks on Steam and the network: call off the main thread. */
    fun fetch(credentials: TokenStore.Credentials): Art {
        val appInfo = SteamSession().use { session ->
            session.logOn(credentials)
            session.appInfo(DepotFetcher.APP_ID)
        } ?: throw RuntimeException("Steam sent no app info")

        // appinfo → common → { name, clienticon, … }
        val common = appInfo.get("common")
        val url = iconUrl(DepotFetcher.APP_ID, common.get("clienticon").asString())
            ?: throw RuntimeException("Steam app info has no client icon")

        val bytes = download(url)
        val bitmap = BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
            ?: throw RuntimeException("Steam icon did not decode")
        return Art(common.get("name").asString(), bitmap)
    }

    /** The CDN address of the client icon [hash], or null when it is not a SHA-1. */
    internal fun iconUrl(appId: Int, hash: String?): String? {
        if (hash == null || !SHA1_HEX.matches(hash)) {
            return null
        }
        return "$CDN/$appId/$hash.ico"
    }

    private fun download(url: String): ByteArray {
        val conn = URL(url).openConnection() as HttpURLConnection
        conn.connectTimeout = TIMEOUT_MS
        conn.readTimeout = TIMEOUT_MS
        try {
            val code = conn.responseCode
            if (code != HttpURLConnection.HTTP_OK) {
                throw RuntimeException("Steam icon download: HTTP $code")
            }
            return conn.inputStream.use { it.readBytes() }
        } finally {
            conn.disconnect()
        }
    }
}
