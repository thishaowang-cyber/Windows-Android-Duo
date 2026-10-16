package com.example.laptophinge

import android.os.Bundle
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import com.example.laptophinge.ui.AngleScreen
import com.example.laptophinge.ui.GlassScreen
import com.example.laptophinge.ui.theme.LaptopHingeTheme

/**
 * 唯一 Activity，也是唯一的界面。
 *
 * ## 为什么不做前台服务 / 后台常驻
 *
 * 目标机型是 vivo X200（OriginOS）。国产定制 ROM 对长时间运行的后台服务
 * 有强管控（后台高耗电白名单、自启动、省电模式三层），要对抗它需要在
 * 系统设置里手动放行，体验脆弱且难以复现。
 *
 * 而本项目的产品形态本来就是「手机固定在 A 面上，屏幕亮着显示角度」——
 * 用户在电脑前抬眼就能看到读数。于是：
 *
 *   - `FLAG_KEEP_SCREEN_ON` 保持屏幕常亮
 *   - 传感器只在前台回调（[onResume] 注册 / [onPause] 注销）
 *
 * 这同时解决了「省电策略杀进程」和「看不到读数」两个问题。
 * 将来若要盖着盖子做长时间发热漂移测试，再补一个前台服务即可，
 * 本类的状态机不依赖任何后台假设。
 */
class MainActivity : ComponentActivity() {

    private val viewModel = HingeViewModel()
    private lateinit var sensors: HingeSensorService

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()

        // 屏幕常亮：前台服务受限的系统上，这是最省事也最可靠的方案
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        // 服务要求一个 Listener，所以必须先于 setContent 构造；
        // 而 Compose 需要读取 viewModel.state，因此 state 是 viewModel 的属性而非局部变量。
        sensors = HingeSensorService(this, viewModel.listener)

        setContent {
            LaptopHingeTheme {
                var showGlass by rememberSaveable { mutableStateOf(false) }
                // 不用 rememberSaveable：这里必须与实际的服务端状态同生共死。
                // 若把它恢复成 true 而服务端并未重建，界面就会显示「已开启」却什么都没发。
                var sending by remember { mutableStateOf(false) }
                var shareAddress by remember { mutableStateOf<String?>(null) }
                if (showGlass) {
                    GlassScreen(
                        state = viewModel.state,
                        onBack = { showGlass = false },
                    )
                } else {
                    AngleScreen(
                        state = viewModel.state,
                        sending = sending,
                        shareAddress = shareAddress,
                        onToggleLogging = { toggleLogging() },
                        onToggleSending = {
                            sending = toggleSending(sending) { shareAddress = it }
                        },
                        onOpenGlass = { showGlass = true },
                    )
                }
            }
        }
    }

    override fun onResume() {
        super.onResume()
        sensors.start()
        viewModel.onRunningChanged(true)
    }

    override fun onPause() {
        super.onPause()
        sensors.stop()
        viewModel.onRunningChanged(false)
    }

    override fun onDestroy() {
        super.onDestroy()
        sensors.release()
    }

    /**
     * 开始/停止 CSV 记录。
     *
     * 写入 `Android/data/<包名>/files/Documents/logs/`——应用专属目录，
     * **不需要任何运行时权限**，也可用 `adb pull` 直接取走：
     *
     * ```
     * adb pull /sdcard/Android/data/com.example.laptophinge/files/Documents/logs
     * ```
     */
    /**
     * 开关「把角度送到电脑」。
     *
     * 手机开 TCP 服务端、电脑主动连过来，所以电脑端不需要任何防火墙规则；
     * 代价是电脑要知道手机地址，因此这里把本机地址回调出去显示在界面上。
     * 返回切换后的状态，供 UI 同步。
     */
    private fun toggleSending(current: Boolean, onAddress: (String?) -> Unit): Boolean {
        if (current) {
            sensors.stopSending()
            onAddress(null)
            viewModel.onMessage("已停止向电脑发送角度")
            return false
        }
        sensors.startSending { text -> viewModel.onMessage(text) }
        val endpoint = sensors.shareAddress?.let { "$it:${sensors.sharePort}" }
        onAddress(endpoint)
        viewModel.onMessage(
            if (endpoint != null) "把 $endpoint 填进电脑端 Windows Duo 的「手机 IP」"
            else "已在监听，但未取到本机地址（请确认已连 Wi-Fi）",
        )
        return true
    }

    private fun toggleLogging() {
        if (sensors.isLogging) {
            val path = sensors.stopLogging()
            viewModel.onLoggingChanged(false, path)
            viewModel.onMessage(
                if (path != null) "日志已保存：$path" else "日志已停止",
            )
        } else {
            sensors.startLogging(this, label = "hinge")
            viewModel.onLoggingChanged(true, null)
            viewModel.onMessage("开始记录 CSV（含全部原始传感器数据，可用 adb pull 取走）")
        }
    }
}
