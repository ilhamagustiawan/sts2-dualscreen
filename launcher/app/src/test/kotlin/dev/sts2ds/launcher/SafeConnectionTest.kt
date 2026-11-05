package dev.sts2ds.launcher

import `in`.dragonbra.javasteam.networking.steam3.Connection
import `in`.dragonbra.javasteam.networking.steam3.DisconnectedEventArgs
import `in`.dragonbra.javasteam.networking.steam3.ProtocolTypes
import org.junit.Assert.assertEquals
import org.junit.Test
import java.net.InetAddress
import java.net.InetSocketAddress

class SafeConnectionTest {
    /** Stands in for WebSocketConnection: lets the test fire its events. */
    private class FakeConnection : Connection() {
        fun fireDisconnected(user: Boolean) = onDisconnected(user)
        override fun connect(endPoint: InetSocketAddress, timeout: Int) {}
        override fun disconnect(userInitiated: Boolean) {}
        override fun send(data: ByteArray) {}
        override fun getLocalIP(): InetAddress? = null
        override fun getCurrentEndPoint(): InetSocketAddress? = null
        override fun getProtocolTypes(): ProtocolTypes = ProtocolTypes.WEB_SOCKET
    }

    @Test
    fun secondDisconnectIsDroppedAndHandlerErrorsAreContained() {
        val inner = FakeConnection()
        val safe = SafeConnection(inner)
        var calls = 0
        // Behaves like CMClient 1.8.0: the second call dereferences a nulled field.
        safe.disconnected.addEventHandler { _, _: DisconnectedEventArgs ->
            calls++
            if (calls > 1) throw NullPointerException("connection is null")
        }

        inner.fireDisconnected(true)
        inner.fireDisconnected(true) // used to crash the launcher

        assertEquals(1, calls)
    }

    @Test
    fun handlerExceptionDoesNotEscape() {
        val inner = FakeConnection()
        val safe = SafeConnection(inner)
        safe.disconnected.addEventHandler { _, _ -> throw NullPointerException() }
        inner.fireDisconnected(false) // must not throw
    }
}
