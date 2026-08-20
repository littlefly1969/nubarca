// Expo config plugin: the NubArca TV native platform bridge.
//
// It carries exactly three capabilities, each of which exists because there is
// no JavaScript binding for it:
//
//   1. finishAndRemoveTask() — the final BACK at the navigation root must really
//      CLOSE the app (see below).
//   2. A user-approved package install — NubArca TV must be able to install its
//      OWN next official APK from inside the app, so a native upgrade no longer
//      needs ADB, a PC or a file manager.
//   3. An AUDIO OUTPUT-ROUTE observer. HDMI unplugged, an AV receiver switched
//      away, a Bluetooth speaker disconnecting: standard Android reports these
//      and a media app is expected to stop rather than keep playing to nowhere.
//      Auditing the installed expo-video found an AudioFocusManager but NO
//      AudioDeviceCallback and no ACTION_AUDIO_BECOMING_NOISY handling, and
//      Media3 leaves setHandleAudioBecomingNoisy off by default — so this gap
//      is real rather than assumed. The observer is deliberately DUMB: it emits
//      a semantic event and nothing else. It does not play, pause, request
//      audio focus, build a MediaSession or change routing, because expo-video
//      already owns every one of those and a second owner is the defect.
//
// WHY (1) EXISTS
// --------------
// At the navigation root the remote's BACK button must CLOSE NubArca TV. React
// Native's `BackHandler.exitApp()` was used for that and did not produce the
// required behaviour on a physical Fire Stick: the launcher came back, but the
// NubArca task stayed in the recents/task list and relaunching resumed the old
// Activity instead of starting a fresh one. `exitApp()` maps to
// `Activity.moveTaskToBack(true)` on Android — it BACKGROUNDS the task by
// design, and no amount of JavaScript changes that.
//
// The correct API is `Activity.finishAndRemoveTask()`: it finishes every
// Activity of the task AND removes the task from recents. It has no JavaScript
// binding in React Native or Expo, so this plugin adds one.
//
// WHAT "CLOSED" MEANS, AND WHAT IT DOES NOT
// -----------------------------------------
// Success is: the Activity is finished, the task is removed, the Fire TV
// launcher is visible, nothing keeps playing, and relaunching creates a NEW
// Activity. Android may keep the process alive as a cached process afterwards —
// that is normal, healthy platform behaviour and is NOT a failure of this
// bridge. Which is why the implementation deliberately does NOT call
// `System.exit`, `Runtime.getRuntime().exit` or `Process.killProcess`: those
// kill the process out from under the platform, corrupt the saved instance
// state, and are the standard cause of a "reopens into a broken screen" bug.
//
// WHY (2) IS SHAPED THE WAY IT IS
// -------------------------------
// The app requests exactly ONE new permission, REQUEST_INSTALL_PACKAGES, which
// is the ordinary permission an app needs to ASK the platform to install a
// package. The user still confirms every install on a Fire OS screen we do not
// control, and that is deliberate: a TV appliance that can silently replace its
// own binary is a far worse product than one that asks. The privileged
// alternatives (INSTALL_PACKAGES, UPDATE_PACKAGES_WITHOUT_USER_ACTION, device
// owner, root, shell) are not merely unused — the manifest mod below FAILS THE
// BUILD if any of them ever appears, from this plugin or any other.
//
// WHY A CONFIG PLUGIN AND NOT AN EDIT TO android/
// -----------------------------------------------
// `expo prebuild` regenerates android/ from the template, so a hand-edited file
// there is deleted on the next release build. The plugin is the tracked source
// of truth and re-applies on every prebuild — the same reasoning as
// withReleaseSigning.

const {
  AndroidConfig,
  withAndroidManifest,
  withDangerousMod,
  withMainApplication,
} = require('expo/config-plugins');
const fs = require('node:fs');
const path = require('node:path');

const PACKAGE_DIR = 'it/littlefly/nubarca/tv/platform';
const PACKAGE_NAME = 'it.littlefly.nubarca.tv.platform';

// The one permission this app asks for. It grants the right to ASK; Fire OS
// still shows its own confirmation for every install.
const INSTALL_PERMISSION = 'android.permission.REQUEST_INSTALL_PACKAGES';

// Privileged install capabilities NubArca TV must never hold. INSTALL_PACKAGES
// is signature/privileged-only, UPDATE_PACKAGES_WITHOUT_USER_ACTION suppresses
// the confirmation this design is built around, and the others are simply not
// something a consumer TV app should be able to reach for.
const FORBIDDEN_PERMISSIONS = [
  'android.permission.INSTALL_PACKAGES',
  'android.permission.UPDATE_PACKAGES_WITHOUT_USER_ACTION',
  'android.permission.DELETE_PACKAGES',
  'android.permission.MANAGE_DEVICE_ADMINS',
];

const MODULE_KT = `package ${PACKAGE_NAME}

import android.app.Activity
import com.facebook.react.bridge.Promise
import com.facebook.react.bridge.ReactApplicationContext
import com.facebook.react.bridge.ReactContextBaseJavaModule
import com.facebook.react.bridge.ReactMethod

/**
 * The NubArca TV platform bridge.
 *
 * TASK EXIT
 * ---------
 * BackHandler.exitApp() maps to Activity.moveTaskToBack(true), which BACKGROUNDS
 * the task: on a Fire Stick the launcher appears but the task stays in recents
 * and a relaunch resumes the old Activity. finishAndRemoveTask() finishes every
 * Activity of the task and removes the task, which is what the product means by
 * "closed".
 *
 * Deliberately no System.exit / Process.killProcess. Killing the process is not
 * how an Android app exits: it skips orderly teardown and leaves the platform
 * holding stale saved state. A cached process surviving after the task is
 * removed is correct platform behaviour, not a leak.
 *
 * Runs on the UI thread because finishAndRemoveTask() is an Activity call.
 * Resolves false when there is no current Activity (nothing to finish) so the
 * JavaScript side can fall back rather than hang.
 *
 * The Activity is read through reactApplicationContext, NOT through the
 * inherited getCurrentActivity(). In React Native 0.85 that inherited member is
 * a Kotlin *function*, so Kotlin's synthetic-property access (\`currentActivity\`)
 * does not resolve against it — only Java getters get that treatment — and it is
 * deprecated in favour of exactly this call.
 *
 * SELF-UPDATE
 * -----------
 * The three install methods are the JavaScript-visible half of
 * NubArcaTvInstaller. They never return an exception, a stack trace or a
 * filesystem path to JavaScript — only the sanitized failure codes the update
 * screen knows how to explain. The heavy work (hashing and streaming a
 * whole APK) runs on its own thread so it can never block the React native-
 * modules queue.
 */
class NubArcaTvPlatformModule(reactContext: ReactApplicationContext) :
    ReactContextBaseJavaModule(reactContext) {

    override fun getName(): String = "NubArcaTvPlatform"

    private val outputObserver = NubArcaTvOutputObserver(reactContext)

    override fun invalidate() {
        outputObserver.stop()
        super.invalidate()
    }

    @ReactMethod
    fun exitAndRemoveTask(promise: Promise) {
        val activity: Activity? = reactApplicationContext.currentActivity
        if (activity == null) {
            promise.resolve(false)
            return
        }
        activity.runOnUiThread {
            activity.finishAndRemoveTask()
        }
        promise.resolve(true)
    }

    /**
     * Begin/stop observing the audio output route. The JS media authority calls
     * these around a playback context; outside one there is nothing to react to
     * and nothing should be registered.
     */
    @ReactMethod
    fun startOutputObserver(promise: Promise) {
        outputObserver.start()
        promise.resolve(true)
    }

    @ReactMethod
    fun stopOutputObserver(promise: Promise) {
        outputObserver.stop()
        promise.resolve(true)
    }

    /** Whether the user has already allowed NubArca TV to request installs. */
    @ReactMethod
    fun canRequestPackageInstalls(promise: Promise) {
        promise.resolve(NubArcaTvInstaller.canRequestPackageInstalls(reactApplicationContext))
    }

    /**
     * Opens the system screen where the user grants that permission, scoped to
     * NubArca TV's own package. Resolves false when no such screen exists.
     */
    @ReactMethod
    fun openPackageInstallSettings(promise: Promise) {
        promise.resolve(NubArcaTvInstaller.openPackageInstallSettings(reactApplicationContext))
    }

    /**
     * Validates the staged APK against the running install and hands it to the
     * platform PackageInstaller.
     *
     * versionCode arrives as a Double because that is the only number the React
     * Native bridge carries; it is range-checked before use.
     *
     * Resolves "installer-launched" once Fire OS has been asked to show its
     * confirmation, or "installed" if the platform reported success first.
     * Rejects with a sanitized code — never a message from a caught exception.
     */
    @ReactMethod
    fun requestPackageUpdate(
        localApkPath: String,
        expectedSha256: String,
        expectedVersionCode: Double,
        promise: Promise,
    ) {
        Thread {
            NubArcaTvInstaller.requestPackageUpdate(
                reactApplicationContext,
                localApkPath,
                expectedSha256,
                expectedVersionCode,
            ) { failureCode, outcome ->
                if (failureCode != null) {
                    promise.reject(failureCode, failureCode)
                } else {
                    promise.resolve(outcome)
                }
            }
        }.start()
    }
}
`;

const INSTALLER_KT = `package ${PACKAGE_NAME}

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageInfo
import android.content.pm.PackageInstaller
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.provider.Settings
import java.io.File
import java.security.MessageDigest
import java.util.concurrent.atomic.AtomicBoolean

/**
 * The product-side install gate for NubArca TV's own updates.
 *
 * WHAT THIS IS FOR
 * ----------------
 * Android will run its own package/signature checks when it commits the
 * session, and those are the real security boundary. Everything here happens
 * EARLIER and FAILS CLOSED, so a wrong or tampered file is refused before an
 * install session exists at all — and so the user is told which gate refused it
 * rather than being dropped into a generic platform error.
 *
 * A matching SHA-256 is never sufficient on its own. It proves only that the
 * bytes are the ones the release descriptor described; it says nothing about
 * what those bytes contain. Package name, versionCode and the SIGNER
 * certificate are therefore read out of the archive itself and compared with the
 * RUNNING install.
 *
 * Signers are compared as full SHA-256 digests over the certificate bytes.
 * Comparing subject/CN labels would accept any self-signed certificate that
 * copied the string.
 *
 * WHAT IT DELIBERATELY DOES NOT DO
 * --------------------------------
 * No silent install. No root, device-owner or shell path. The commit always
 * asks the platform for user confirmation, and on Android 12+ it says so
 * explicitly with USER_ACTION_REQUIRED. A TV appliance that can replace its own
 * binary without asking is a worse product, not a better one.
 */
internal object NubArcaTvInstaller {
    const val ERROR_PERMISSION_REQUIRED = "permission-required"
    const val ERROR_INVALID_FILE = "invalid-file"
    const val ERROR_HASH_MISMATCH = "hash-mismatch"
    const val ERROR_WRONG_PACKAGE = "wrong-package"
    const val ERROR_NOT_NEWER = "not-newer"
    const val ERROR_SIGNER_MISMATCH = "signer-mismatch"
    const val ERROR_INSTALLER_REJECTED = "installer-rejected"
    const val ERROR_INSTALLER_UNAVAILABLE = "installer-unavailable"

    private const val OUTCOME_LAUNCHED = "installer-launched"
    private const val OUTCOME_INSTALLED = "installed"
    private const val SESSION_FILE = "nubarca-tv-update.apk"
    private val HEX = "0123456789abcdef".toCharArray()
    private val SHA256_HEX = Regex("^[0-9a-f]{64}$")

    private class GateFailure(val code: String) : Exception(code)

    /**
     * Below Android 8 there is no per-app install permission at all, so there is
     * nothing to ask for and nothing to check.
     */
    fun canRequestPackageInstalls(context: Context): Boolean =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            context.packageManager.canRequestPackageInstalls()
        } else {
            true
        }

    /** Opens the unknown-app-sources screen for THIS package only. */
    fun openPackageInstallSettings(context: Context): Boolean {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return false
        val intent = Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES)
            .setData(Uri.parse("package:" + context.packageName))
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        return try {
            context.startActivity(intent)
            true
        } catch (error: Exception) {
            false
        }
    }

    fun requestPackageUpdate(
        context: Context,
        localApkPath: String,
        expectedSha256: String,
        expectedVersionCode: Double,
        complete: (String?, String?) -> Unit,
    ) {
        val staged: File
        try {
            if (!canRequestPackageInstalls(context)) throw GateFailure(ERROR_PERMISSION_REQUIRED)
            staged = resolveStagedApk(context, localApkPath)
            validate(context, staged, expectedSha256, expectedVersionCode)
        } catch (failure: GateFailure) {
            complete(failure.code, null)
            return
        } catch (error: Exception) {
            complete(ERROR_INVALID_FILE, null)
            return
        }
        startInstall(context, staged, complete)
    }

    // --- validation ----------------------------------------------------------

    /**
     * The APK must be a real file inside storage NubArca TV owns. A path handed
     * in from JavaScript is not trusted: it is canonicalised first, so
     * \`cache/../../../somewhere\` cannot escape, and then required to live under
     * one of this application's own directories.
     */
    private fun resolveStagedApk(context: Context, rawPath: String): File {
        val path = if (rawPath.startsWith("file://")) {
            Uri.parse(rawPath).path ?: throw GateFailure(ERROR_INVALID_FILE)
        } else {
            rawPath
        }
        val file = File(path).canonicalFile
        val roots = listOfNotNull(context.cacheDir, context.filesDir, context.noBackupFilesDir)
            .map { it.canonicalFile.path + File.separator }
        if (roots.none { file.path.startsWith(it) }) throw GateFailure(ERROR_INVALID_FILE)
        if (!file.isFile || !file.canRead() || file.length() <= 0L) {
            throw GateFailure(ERROR_INVALID_FILE)
        }
        return file
    }

    private fun validate(
        context: Context,
        file: File,
        expectedSha256: String,
        expectedVersionCode: Double,
    ) {
        if (!SHA256_HEX.matches(expectedSha256)) throw GateFailure(ERROR_INVALID_FILE)
        if (expectedVersionCode < 1.0 || expectedVersionCode != Math.floor(expectedVersionCode)) {
            throw GateFailure(ERROR_INVALID_FILE)
        }
        if (sha256(file) != expectedSha256) throw GateFailure(ERROR_HASH_MISMATCH)

        val candidate = readArchive(context, file)
        if (candidate.packageName != context.packageName) throw GateFailure(ERROR_WRONG_PACKAGE)

        // A candidate whose real versionCode differs from the one the descriptor
        // advertised means the descriptor does not describe these bytes. That is
        // a bad file, not a stale version.
        val candidateCode = versionCodeOf(candidate)
        if (candidateCode != expectedVersionCode.toLong()) throw GateFailure(ERROR_INVALID_FILE)

        val installed = installedPackageInfo(context)
        if (candidateCode <= versionCodeOf(installed)) throw GateFailure(ERROR_NOT_NEWER)

        val installedSigners = signerDigests(installed)
        val candidateSigners = signerDigests(candidate)
        if (installedSigners.isEmpty() || candidateSigners.isEmpty()
            || installedSigners != candidateSigners
        ) {
            throw GateFailure(ERROR_SIGNER_MISMATCH)
        }
    }

    private fun sha256(file: File): String {
        val digest = MessageDigest.getInstance("SHA-256")
        file.inputStream().use { input ->
            val buffer = ByteArray(64 * 1024)
            while (true) {
                val read = input.read(buffer)
                if (read <= 0) break
                digest.update(buffer, 0, read)
            }
        }
        return toHex(digest.digest())
    }

    private fun toHex(bytes: ByteArray): String {
        val out = CharArray(bytes.size * 2)
        for (index in bytes.indices) {
            val value = bytes[index].toInt() and 0xff
            out[index * 2] = HEX[value ushr 4]
            out[index * 2 + 1] = HEX[value and 0x0f]
        }
        return String(out)
    }

    private fun signatureFlag(): Int =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            PackageManager.GET_SIGNING_CERTIFICATES
        } else {
            @Suppress("DEPRECATION")
            PackageManager.GET_SIGNATURES
        }

    private fun readArchive(context: Context, file: File): PackageInfo =
        context.packageManager.getPackageArchiveInfo(file.path, signatureFlag())
            ?: throw GateFailure(ERROR_INVALID_FILE)

    private fun installedPackageInfo(context: Context): PackageInfo =
        try {
            context.packageManager.getPackageInfo(context.packageName, signatureFlag())
        } catch (error: PackageManager.NameNotFoundException) {
            throw GateFailure(ERROR_WRONG_PACKAGE)
        }

    private fun versionCodeOf(info: PackageInfo): Long =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            info.longVersionCode
        } else {
            @Suppress("DEPRECATION")
            info.versionCode.toLong()
        }

    /**
     * SHA-256 over each signing certificate's DER bytes.
     *
     * On API 28+ this reads the signers of the APK CONTENTS, which is the set
     * that must match for an in-place update, rather than the rotation history.
     */
    private fun signerDigests(info: PackageInfo): Set<String> {
        val signatures = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            info.signingInfo?.apkContentsSigners
        } else {
            @Suppress("DEPRECATION")
            info.signatures
        }
        if (signatures == null) return emptySet()
        val digests = HashSet<String>()
        for (signature in signatures) {
            if (signature == null) continue
            digests.add(toHex(MessageDigest.getInstance("SHA-256").digest(signature.toByteArray())))
        }
        return digests
    }

    // --- install -------------------------------------------------------------

    /**
     * Streams the validated APK into a PackageInstaller session and commits it
     * with a package-scoped status receiver.
     *
     * The staged copy is deleted as soon as its bytes are inside the session:
     * the session owns them from that point, and leaving a second copy of a
     * whole APK in the cache is exactly the unbounded growth this must not
     * cause. A later failure means re-downloading, which is the cheaper mistake.
     *
     * The app may be replaced and killed as part of a successful self-update, so
     * nothing here depends on JavaScript ever seeing STATUS_SUCCESS. On the next
     * launch the installed versionCode is the authority.
     */
    private fun startInstall(context: Context, file: File, complete: (String?, String?) -> Unit) {
        val installer = try {
            context.packageManager.packageInstaller
        } catch (error: Exception) {
            complete(ERROR_INSTALLER_UNAVAILABLE, null)
            return
        }
        val action = context.packageName + ".PACKAGE_INSTALL_STATUS"
        val delivered = AtomicBoolean(false)
        var receiver: BroadcastReceiver? = null
        val finish = fun(code: String?, outcome: String?) {
            if (!delivered.compareAndSet(false, true)) return
            receiver?.let { registered ->
                try {
                    context.unregisterReceiver(registered)
                } catch (error: Exception) {
                    // Already gone; nothing to undo.
                }
            }
            complete(code, outcome)
        }

        receiver = object : BroadcastReceiver() {
            override fun onReceive(receiverContext: Context, intent: Intent) {
                if (intent.action != action) return
                val status = intent.getIntExtra(
                    PackageInstaller.EXTRA_STATUS,
                    PackageInstaller.STATUS_FAILURE,
                )
                when (status) {
                    PackageInstaller.STATUS_PENDING_USER_ACTION -> {
                        val confirmation = confirmationIntent(intent)
                        if (confirmation == null) {
                            finish(ERROR_INSTALLER_REJECTED, null)
                            return
                        }
                        confirmation.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                        try {
                            receiverContext.startActivity(confirmation)
                        } catch (error: Exception) {
                            finish(ERROR_INSTALLER_UNAVAILABLE, null)
                            return
                        }
                        finish(null, OUTCOME_LAUNCHED)
                    }
                    PackageInstaller.STATUS_SUCCESS -> finish(null, OUTCOME_INSTALLED)
                    else -> finish(ERROR_INSTALLER_REJECTED, null)
                }
            }
        }

        val filter = IntentFilter(action)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            context.registerReceiver(receiver, filter, Context.RECEIVER_NOT_EXPORTED)
        } else {
            @Suppress("UnspecifiedRegisterReceiverFlag")
            context.registerReceiver(receiver, filter)
        }

        var sessionId = -1
        try {
            val params = PackageInstaller.SessionParams(
                PackageInstaller.SessionParams.MODE_FULL_INSTALL,
            )
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                params.setAppPackageName(context.packageName)
            }
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                // Say out loud what this design already accepts: the user
                // confirms. Never USER_ACTION_NOT_REQUIRED.
                params.setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_REQUIRED)
            }
            sessionId = installer.createSession(params)
            installer.openSession(sessionId).use { session ->
                session.openWrite(SESSION_FILE, 0, file.length()).use { output ->
                    file.inputStream().use { input -> input.copyTo(output) }
                    session.fsync(output)
                }
                file.delete()
                val statusIntent = Intent(action).setPackage(context.packageName)
                var flags = PendingIntent.FLAG_UPDATE_CURRENT
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                    // The platform fills the status extras in, so this one
                    // PendingIntent must stay mutable.
                    flags = flags or PendingIntent.FLAG_MUTABLE
                }
                val pending = PendingIntent.getBroadcast(context, sessionId, statusIntent, flags)
                session.commit(pending.intentSender)
            }
        } catch (error: Exception) {
            if (sessionId >= 0) {
                try {
                    installer.abandonSession(sessionId)
                } catch (abandonError: Exception) {
                    // Nothing further to do; the session is already unusable.
                }
            }
            file.delete()
            finish(ERROR_INSTALLER_UNAVAILABLE, null)
        }
    }

    private fun confirmationIntent(intent: Intent): Intent? =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            intent.getParcelableExtra(Intent.EXTRA_INTENT, Intent::class.java)
        } else {
            @Suppress("DEPRECATION")
            intent.getParcelableExtra(Intent.EXTRA_INTENT) as? Intent
        }
}
`;


const OUTPUT_KT = `package ${PACKAGE_NAME}

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.media.AudioDeviceCallback
import android.media.AudioDeviceInfo
import android.media.AudioManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import com.facebook.react.bridge.ReactApplicationContext
import com.facebook.react.modules.core.DeviceEventManagerModule

/**
 * Observes the AUDIO OUTPUT ROUTE and reports its loss. Nothing else.
 *
 * WHAT IT IS FOR
 * --------------
 * A television's sound can go away without the app being backgrounded: HDMI
 * unplugged, an AV receiver switched to another input, a Bluetooth speaker
 * walking out of range. Android reports all three; a media application is
 * expected to stop rather than keep playing to an output nobody can hear.
 *
 * WHAT IT DELIBERATELY DOES NOT DO
 * --------------------------------
 * It does not pause, play, seek, request or abandon audio focus, build a
 * MediaSession, or change routing. expo-video/Media3 already owns playback,
 * audio focus and the session, and the whole point of the preceding audit was
 * that a SECOND owner of any of those is the defect, not the fix. This class
 * emits one semantic event and lets the existing JavaScript playback authority
 * decide what it means.
 *
 * Two standard sources, because they catch different things:
 *   * ACTION_AUDIO_BECOMING_NOISY — the classic "output is about to go away"
 *     broadcast, which fires for a headset/Bluetooth disconnect;
 *   * AudioDeviceCallback.onAudioDevicesRemoved — narrowed to the TV DISPLAY
 *     PATH (HDMI / ARC / eARC). It reports every output that vanishes, not the
 *     one we are using, so a wider net pauses playback because some unrelated
 *     speaker left the room. Bluetooth and headset route loss stays with
 *     BECOMING_NOISY, which is Android's statement about the ACTIVE route.
 */
internal class NubArcaTvOutputObserver(
    private val reactContext: ReactApplicationContext,
) {
    companion object {
        const val EVENT_OUTPUT_LOST = "NubArcaTvOutputLost"
    }

    private val handler = Handler(Looper.getMainLooper())
    private var registered = false

    private val noisyReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            if (intent.action == AudioManager.ACTION_AUDIO_BECOMING_NOISY) emit()
        }
    }

    private val deviceCallback = object : AudioDeviceCallback() {
        override fun onAudioDevicesRemoved(removed: Array<out AudioDeviceInfo>?) {
            if (removed == null) return
            for (device in removed) {
                if (isDisplayPathOutput(device.type)) {
                    emit()
                    return
                }
            }
        }
    }

    /**
     * The TV DISPLAY PATH only.
     *
     * onAudioDevicesRemoved reports every output that disappears, not the one
     * NubArca is using. Treating any removed Bluetooth speaker, headset or USB
     * dongle as "our route is gone" pauses playback for a device that was never
     * carrying it — a false positive the user experiences as the video stopping
     * for no reason.
     *
     * HDMI is different: on a television it IS the path the picture and sound
     * travel, so losing it genuinely means playback has nowhere to go.
     * Everything else is left to ACTION_AUDIO_BECOMING_NOISY, which is
     * Android's own statement that the ACTIVE route is becoming unusable —
     * exactly the question this callback cannot answer.
     */
    private fun isDisplayPathOutput(type: Int): Boolean {
        if (type == AudioDeviceInfo.TYPE_HDMI || type == AudioDeviceInfo.TYPE_HDMI_ARC) {
            return true
        }
        // eARC exists only from API 31; referencing the constant unguarded
        // would not compile against older platforms.
        return Build.VERSION.SDK_INT >= Build.VERSION_CODES.S &&
            type == AudioDeviceInfo.TYPE_HDMI_EARC
    }

    fun start() {
        if (registered) return
        registered = true
        val filter = IntentFilter(AudioManager.ACTION_AUDIO_BECOMING_NOISY)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            reactContext.registerReceiver(noisyReceiver, filter, Context.RECEIVER_NOT_EXPORTED)
        } else {
            @Suppress("UnspecifiedRegisterReceiverFlag")
            reactContext.registerReceiver(noisyReceiver, filter)
        }
        audioManager()?.registerAudioDeviceCallback(deviceCallback, handler)
    }

    fun stop() {
        if (!registered) return
        registered = false
        try {
            reactContext.unregisterReceiver(noisyReceiver)
        } catch (error: Exception) {
            // Already gone; nothing to undo.
        }
        audioManager()?.unregisterAudioDeviceCallback(deviceCallback)
    }

    private fun audioManager(): AudioManager? =
        reactContext.getSystemService(Context.AUDIO_SERVICE) as? AudioManager

    private fun emit() {
        if (!reactContext.hasActiveReactInstance()) return
        reactContext
            .getJSModule(DeviceEventManagerModule.RCTDeviceEventEmitter::class.java)
            .emit(EVENT_OUTPUT_LOST, null)
    }
}
`;

const PACKAGE_KT = `package ${PACKAGE_NAME}

import android.view.View
import com.facebook.react.ReactPackage
import com.facebook.react.bridge.NativeModule
import com.facebook.react.bridge.ReactApplicationContext
import com.facebook.react.uimanager.ReactShadowNode
import com.facebook.react.uimanager.ViewManager

/** Registers the single platform module. No view managers, no other natives. */
class NubArcaTvPlatformPackage : ReactPackage {
    override fun createNativeModules(
        reactContext: ReactApplicationContext
    ): List<NativeModule> = listOf(NubArcaTvPlatformModule(reactContext))

    override fun createViewManagers(
        reactContext: ReactApplicationContext
    ): List<ViewManager<View, ReactShadowNode<*>>> = emptyList()
}
`;

// Written into MainApplication's package list. The Expo template builds it as
// `PackageList(this).packages.apply { … }` with a commented `add(...)`
// placeholder inside the block; appending after that comment is the documented
// extension point.
const PACKAGE_LIST_ANCHOR = '// add(MyReactNativePackage())';
const PACKAGE_LIST_REPLACEMENT = `// add(MyReactNativePackage())
          // NubArca TV: the finishAndRemoveTask bridge, so the final BACK at
          // the navigation root really closes the task rather than
          // backgrounding it, and the user-approved self-update installer.
          // See plugins/withTvPlatformModule.js.
          add(it.littlefly.nubarca.tv.platform.NubArcaTvPlatformPackage())`;

const withNativeSources = (config) =>
  withDangerousMod(config, [
    'android',
    (modConfig) => {
      const root = modConfig.modRequest.platformProjectRoot;
      const dir = path.join(root, 'app', 'src', 'main', 'java', PACKAGE_DIR);
      fs.mkdirSync(dir, { recursive: true });
      fs.writeFileSync(path.join(dir, 'NubArcaTvPlatformModule.kt'), MODULE_KT, 'utf8');
      fs.writeFileSync(path.join(dir, 'NubArcaTvInstaller.kt'), INSTALLER_KT, 'utf8');
      fs.writeFileSync(path.join(dir, 'NubArcaTvOutputObserver.kt'), OUTPUT_KT, 'utf8');
      fs.writeFileSync(path.join(dir, 'NubArcaTvPlatformPackage.kt'), PACKAGE_KT, 'utf8');
      return modConfig;
    },
  ]);

const withPackageRegistration = (config) =>
  withMainApplication(config, (mainConfig) => {
    const contents = mainConfig.modResults.contents;
    if (contents.includes('NubArcaTvPlatformPackage')) {
      return mainConfig; // already applied
    }
    if (!contents.includes(PACKAGE_LIST_ANCHOR)) {
      // Fail loudly rather than produce an APK whose BACK-at-root silently does
      // nothing: a missing bridge is exactly the defect this plugin fixes.
      throw new Error(
        'withTvPlatformModule: the package list in MainApplication no longer matches ' +
          'the expected template. Refusing to continue, because skipping registration ' +
          'would ship an app whose final BACK cannot close the task.',
      );
    }
    mainConfig.modResults.contents = contents.replace(
      PACKAGE_LIST_ANCHOR,
      PACKAGE_LIST_REPLACEMENT,
    );
    return mainConfig;
  });

// A task that is removed must not be resurrected from a stale snapshot on the
// next launch. `excludeFromRecents` is deliberately NOT set (the user should see
// NubArca in recents while it is running); `alwaysRetainTaskState` is left at
// its default so the platform is free to drop the removed task's state.
const withLauncherActivity = (config) =>
  withAndroidManifest(config, (manifestConfig) => {
    const activity = AndroidConfig.Manifest.getMainActivityOrThrow(manifestConfig.modResults);
    // Relaunching after finishAndRemoveTask must start a clean Activity rather
    // than restore a half-torn-down one.
    activity.$['android:clearTaskOnLaunch'] = 'true';
    return manifestConfig;
  });

// Declares the ONE install permission, exactly once, and refuses to build if a
// privileged install capability has appeared from anywhere. The negative half is
// the point: "we do not silently install" is a property of the shipped manifest,
// not of a comment, and this is where it is enforced.
const withInstallPermission = (config) =>
  withAndroidManifest(config, (manifestConfig) => {
    const manifest = manifestConfig.modResults.manifest;
    const permissions = manifest['uses-permission'] ?? [];
    const names = permissions.map((entry) => entry?.$?.['android:name']);
    for (const forbidden of FORBIDDEN_PERMISSIONS) {
      if (names.includes(forbidden)) {
        throw new Error(
          `withTvPlatformModule: ${forbidden} must never be declared by NubArca TV. ` +
            'Self-update is deliberately user-confirmed; refusing to build a privileged ' +
            'or silent installer.',
        );
      }
    }
    if (!names.includes(INSTALL_PERMISSION)) {
      permissions.push({ $: { 'android:name': INSTALL_PERMISSION } });
    }
    manifest['uses-permission'] = permissions;
    return manifestConfig;
  });

/** @type {import('expo/config-plugins').ConfigPlugin} */
const withTvPlatformModule = (config) =>
  withInstallPermission(withLauncherActivity(withPackageRegistration(withNativeSources(config))));

module.exports = withTvPlatformModule;
