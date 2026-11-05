// SafeConnection — wraps a JavaSteam CM connection so a disconnect can't
// take the process down.
//
// In JavaSteam 1.8.0, WebSocketConnection.disconnect() launches a coroutine
// and returns, and CMClient.connect() calls disconnect() again itself. A
// "give up on this CM and try another" therefore reports the same connection
// as disconnected twice, on two IO threads. CMClient's handler nulls its
// connection field on the first report and dereferences it on the second,
// and that NullPointerException is uncaught on a coroutine thread, so the
// launcher dies. (Fixed upstream after 1.8.0 by a null check.)
//
// This wrapper passes the first disconnect through, drops any later ones,
// and catches whatever CMClient's handlers throw.

package dev.sts2ds.launcher

import `in`.dragonbra.javasteam.networking.steam3.Connection
import `in`.dragonbra.javasteam.networking.steam3.IConnectionFactory
import `in`.dragonbra.javasteam.networking.steam3.ProtocolTypes
import java.net.InetAddress
import java.net.InetSocketAddress
import java.util.concurrent.atomic.AtomicBoolean

class SafeConnection(private val inner: Connection) : Connection() {
    private val disconnectReported = AtomicBoolean(false)

    init {
        inner.netMsgReceived.addEventHandler { _, e -> guarded("message") { onNetMsgReceived(e) } }
        inner.connected.addEventHandler { _, _ -> guarded("connect") { onConnected() } }
        inner.disconnected.addEventHandler { _, e ->
            if (disconnectReported.compareAndSet(false, true)) {
                guarded("disconnect") { onDisconnected(e.isUserInitiated) }
            }
        }
    }

    private inline fun guarded(what: String, block: () -> Unit) {
        try {
            block()
        } catch (t: Throwable) {
            LauncherLog.warn("Steam connection $what handler failed: $t")
        }
    }

    override fun connect(endPoint: InetSocketAddress, timeout: Int) = inner.connect(endPoint, timeout)
    override fun disconnect(userInitiated: Boolean) = inner.disconnect(userInitiated)
    override fun send(data: ByteArray) = inner.send(data)
    override fun getLocalIP(): InetAddress? = inner.localIP
    override fun getCurrentEndPoint(): InetSocketAddress? = inner.currentEndPoint
    override fun getProtocolTypes(): ProtocolTypes = inner.protocolTypes

    companion object {
        val FACTORY = IConnectionFactory { config, protocols ->
            IConnectionFactory.DEFAULT.createConnection(config, protocols)?.let(::SafeConnection)
        }
    }
}
