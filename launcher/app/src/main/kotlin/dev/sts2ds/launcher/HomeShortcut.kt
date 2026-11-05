// HomeShortcut — a home-screen shortcut to the launcher wearing the game's own icon.
//
// The app icon is fixed at build time, and nothing game-derived ships in the
// APK, so it cannot be the game's. A pinned shortcut's icon is a runtime
// bitmap, so it can: GameIcon fetches it from the user's own Steam account.
// The shortcut opens the launcher, same as the app icon.
//
//   ┌──────── 108 ────────┐   Adaptive bitmap: the icon fills the middle
//   │ ┌───── 72 ──────┐   │   72 of 108, which is all any launcher mask
//   │ │     icon      │   │   shows; the rim, seen only in parallax, is
//   │ └───────────────┘   │   the launcher's own dark surface colour.
//   └─────────────────────┘

package dev.sts2ds.launcher

import android.content.Context
import android.content.Intent
import android.content.pm.ShortcutInfo
import android.content.pm.ShortcutManager
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.Rect
import android.graphics.drawable.Icon
import dev.sts2ds.R
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

object HomeShortcut {

    enum class Result { REQUESTED, UPDATED, UNSUPPORTED }

    private const val ID = "game"

    /** 108 dp and its visible 72 dp, at xxxhdpi. */
    private const val FULL_PX = 432
    private const val VISIBLE_PX = 288

    /** Fetches the icon from Steam, then asks the home screen to pin it. */
    suspend fun add(context: Context, credentials: TokenStore.Credentials): Result {
        val shortcuts = context.getSystemService(ShortcutManager::class.java)
        if (shortcuts == null || !shortcuts.isRequestPinShortcutSupported) {
            return Result.UNSUPPORTED
        }

        val art = withContext(Dispatchers.IO) { GameIcon.fetch(credentials) }
        val info = ShortcutInfo.Builder(context, ID)
            .setShortLabel(art.name ?: context.getString(R.string.app_name))
            .setIcon(Icon.createWithAdaptiveBitmap(adaptive(context, art.bitmap)))
            .setIntent(Intent(context, LauncherActivity::class.java).setAction(Intent.ACTION_MAIN))
            .build()

        // Already pinned: refresh it in place rather than asking again.
        if (shortcuts.pinnedShortcuts.any { it.id == ID }) {
            shortcuts.updateShortcuts(listOf(info))
            return Result.UPDATED
        }
        shortcuts.requestPinShortcut(info, null)
        return Result.REQUESTED
    }

    private fun adaptive(context: Context, icon: Bitmap): Bitmap {
        val out = Bitmap.createBitmap(FULL_PX, FULL_PX, Bitmap.Config.ARGB_8888)
        val canvas = Canvas(out)
        canvas.drawColor(context.getColor(R.color.surface))

        val rim = (FULL_PX - VISIBLE_PX) / 2
        val target = Rect(rim, rim, rim + VISIBLE_PX, rim + VISIBLE_PX)
        canvas.drawBitmap(icon, null, target, Paint(Paint.FILTER_BITMAP_FLAG))
        return out
    }
}
