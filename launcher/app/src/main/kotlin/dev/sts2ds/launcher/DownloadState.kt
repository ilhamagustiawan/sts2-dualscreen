// DownloadState — what DownloadService is doing, for whoever is looking.
//
// Process-wide: the service and the launcher both run in ":launcher", so a
// launcher that was closed and reopened mid-download picks the progress up
// from here rather than from the service.

package dev.sts2ds.launcher

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

object DownloadState {

    sealed class State {
        data object Idle : State()
        /** [fraction] is 0f..1f across the depot. */
        data class Running(val fraction: Float, val bytes: Long, val status: String?) : State()
        data class Preparing(val step: String) : State()
        data object Done : State()
        data object Cancelled : State()
        data class Failed(val message: String, val authExpired: Boolean) : State()
    }

    private val state = MutableStateFlow<State>(State.Idle)
    val flow: StateFlow<State> = state.asStateFlow()

    val isActive: Boolean
        get() = state.value.let { it is State.Running || it is State.Preparing }

    internal fun set(value: State) {
        state.value = value
    }

    /** Back to Idle once the launcher has shown a finished, failed or cancelled run. */
    fun acknowledge() {
        if (!isActive) state.value = State.Idle
    }
}
