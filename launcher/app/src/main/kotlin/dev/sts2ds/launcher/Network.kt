// Network — is there a connection worth trying Steam on?
//
// Steam's own timeouts are twenty seconds a step, so asking first is what
// makes "Play" with no connection start the game at once instead of after
// a minute of retries.

package dev.sts2ds.launcher

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities

object Network {
    fun isOnline(context: Context): Boolean {
        val cm = context.getSystemService(ConnectivityManager::class.java) ?: return true
        val caps = cm.getNetworkCapabilities(cm.activeNetwork) ?: return false
        return caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
    }
}
