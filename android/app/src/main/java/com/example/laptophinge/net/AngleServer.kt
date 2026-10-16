package com.example.laptophinge.net

import android.util.Log
import java.io.BufferedWriter
import java.io.OutputStreamWriter
import java.net.Inet4Address
import java.net.NetworkInterface
import java.net.ServerSocket
import java.net.Socket
import java.util.Collections
import java.util.Locale
import java.util.concurrent.CopyOnWriteArrayList
import kotlin.concurrent.thread

/**
 * 把开合角通过局域网送到电脑。
 *
 * ## 为什么手机当服务端、电脑当客户端
 *
 * Windows 防火墙默认**拦截入站**、放行出站，而回程流量在已建立的连接上是状态化
 * 放行的。所以让电脑主动连手机，就完全不需要在电脑上加任何防火墙规则——这是这条
 * 链路唯一不需要改动系统设置的拓扑。
 *
 * 代价是电脑得知道手机地址。手机自身知道自己在局域网里的 IP，因此启动时把它显示
 * 在界面上，由用户在电脑端填一次即可（之后会记住）。
 *
 * ## 线程
 *
 * 接受连接、写数据各自跑在守护线程上。传感器以 100 Hz 调用 [submit]，但这里只写
 * 一个 volatile 字段——**绝不在传感器线程上做 socket 写**，否则对端读得慢就会把
 * 传感器回调堵住，UI 直接卡顿。
 */
class AngleServer(private val onStatus: (String) -> Unit) {

    companion object {
        /** 电脑连接的端口。 */
        const val PORT = 45680

        private const val TAG = "DuoAngleServer"

        /** 发送限流：约 30 Hz。100 Hz 对这条链路没有意义，只会白耗电。 */
        private const val SEND_INTERVAL_MS = 33L
    }

    private class Client(val socket: Socket, val writer: BufferedWriter) {
        fun close() = runCatching { socket.close() }.let { }
    }

    @Volatile private var running = false
    private var server: ServerSocket? = null
    private var acceptThread: Thread? = null
    private var writeThread: Thread? = null

    private val clients = CopyOnWriteArrayList<Client>()

    @Volatile private var latestAngle = 0.0
    @Volatile private var hasAngle = false
    private var lastStatus = ""

    /** 本机在局域网里的地址，供用户在电脑上填写。 */
    val address: String? get() = wifiAddress()

    val port: Int get() = PORT

    fun start() {
        if (running) return
        running = true
        try {
            server = ServerSocket(PORT).apply { reuseAddress = true }
        } catch (t: Throwable) {
            Log.w(TAG, "cannot listen on $PORT", t)
            report("无法监听端口 $PORT：${t.message}")
            running = false
            return
        }
        acceptThread = thread(name = "duo-angle-accept", isDaemon = true) { acceptLoop() }
        writeThread = thread(name = "duo-angle-write", isDaemon = true) { writeLoop() }
        Log.i(TAG, "listening on $PORT, local address ${wifiAddress()}")
        reportStatus()
    }

    fun stop() {
        running = false
        runCatching { server?.close() }
        server = null
        for (c in clients) c.close()
        clients.clear()
        acceptThread?.interrupt(); acceptThread = null
        writeThread?.interrupt(); writeThread = null
    }

    /** 传感器线程调用：只记录最新值，不碰 socket。 */
    fun submit(angleDeg: Double) {
        latestAngle = angleDeg
        hasAngle = true
    }

    private fun acceptLoop() {
        while (running) {
            val socket = try {
                server?.accept() ?: break
            } catch (t: Throwable) {
                if (running) Log.w(TAG, "accept failed", t)
                break
            }
            try {
                // 角度是小包高频，Nagle 会把它们攒起来，直接关掉。
                socket.tcpNoDelay = true
                val writer = BufferedWriter(OutputStreamWriter(socket.getOutputStream(), Charsets.US_ASCII))
                clients.add(Client(socket, writer))
                Log.i(TAG, "client connected: ${socket.inetAddress?.hostAddress}")
                reportStatus()
            } catch (t: Throwable) {
                Log.w(TAG, "client setup failed", t)
                runCatching { socket.close() }
            }
        }
    }

    private fun writeLoop() {
        while (running) {
            if (hasAngle && clients.isNotEmpty()) {
                val line = String.format(Locale.US, "angle=%.2f\n", latestAngle)
                val dead = ArrayList<Client>()
                for (c in clients) {
                    try {
                        c.writer.write(line)
                        c.writer.flush()
                    } catch (t: Throwable) {
                        dead.add(c)
                    }
                }
                if (dead.isNotEmpty()) {
                    clients.removeAll(dead)
                    for (c in dead) c.close()
                    reportStatus()
                }
            }
            try {
                Thread.sleep(SEND_INTERVAL_MS)
            } catch (_: InterruptedException) {
                break
            }
        }
    }

    private fun reportStatus() {
        val local = wifiAddress() ?: "?"
        val text = if (clients.isEmpty())
            "等待电脑连接 · 本机地址 $local:$PORT"
        else
            "已连接 ${clients.size} 台电脑 · $local:$PORT"
        if (text == lastStatus) return
        lastStatus = text
        onStatus(text)
    }

    private fun report(text: String) {
        lastStatus = text
        onStatus(text)
    }

    /** 局域网 IPv4：跳过回环和点对点接口。 */
    private fun wifiAddress(): String? = runCatching {
        for (nif in Collections.list(NetworkInterface.getNetworkInterfaces())) {
            if (!nif.isUp || nif.isLoopback) continue
            for (addr in Collections.list(nif.inetAddresses)) {
                if (addr is Inet4Address && !addr.isLoopbackAddress && addr.isSiteLocalAddress) {
                    return@runCatching addr.hostAddress
                }
            }
        }
        null
    }.getOrNull()
}
