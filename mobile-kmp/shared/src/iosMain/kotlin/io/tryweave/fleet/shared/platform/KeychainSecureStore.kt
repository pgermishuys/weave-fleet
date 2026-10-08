package io.tryweave.fleet.shared.platform

import kotlinx.cinterop.BetaInteropApi
import kotlinx.cinterop.ExperimentalForeignApi
import kotlinx.cinterop.alloc
import kotlinx.cinterop.memScoped
import kotlinx.cinterop.ptr
import kotlinx.cinterop.value
import platform.CoreFoundation.CFDictionaryAddValue
import platform.CoreFoundation.CFDictionaryCreateMutable
import platform.CoreFoundation.CFMutableDictionaryRef
import platform.CoreFoundation.CFRelease
import platform.CoreFoundation.CFTypeRefVar
import platform.CoreFoundation.kCFAllocatorDefault
import platform.CoreFoundation.kCFBooleanTrue
import platform.CoreFoundation.kCFTypeDictionaryKeyCallBacks
import platform.CoreFoundation.kCFTypeDictionaryValueCallBacks
import platform.Foundation.CFBridgingRelease
import platform.Foundation.CFBridgingRetain
import platform.Foundation.NSData
import platform.Foundation.NSString
import platform.Foundation.NSUTF8StringEncoding
import platform.Foundation.create
import platform.Foundation.dataUsingEncoding
import platform.Security.SecItemAdd
import platform.Security.SecItemCopyMatching
import platform.Security.SecItemDelete
import platform.Security.errSecSuccess
import platform.Security.kSecAttrAccessible
import platform.Security.kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
import platform.Security.kSecAttrAccount
import platform.Security.kSecAttrService
import platform.Security.kSecClass
import platform.Security.kSecClassGenericPassword
import platform.Security.kSecMatchLimit
import platform.Security.kSecMatchLimitOne
import platform.Security.kSecReturnData
import platform.Security.kSecValueData

/**
 * The iOS Keychain, generic passwords under one service. "After first unlock, this device only": a notification
 * action can read the token while the phone is locked, and it never syncs to another device.
 *
 * Kotlin/Native talks to the Security framework through CoreFoundation, so the query is a CFDictionary built by hand
 * (a Kotlin Map can't hold CFStringRef keys).
 */
@OptIn(ExperimentalForeignApi::class, BetaInteropApi::class)
class KeychainSecureStore(private val service: String = "io.tryweave.fleet") : SecureStore {

    private fun query(key: String, extra: (CFMutableDictionaryRef?) -> Unit = {}): CFMutableDictionaryRef? {
        val dict = CFDictionaryCreateMutable(kCFAllocatorDefault, 0, kCFTypeDictionaryKeyCallBacks.ptr, kCFTypeDictionaryValueCallBacks.ptr)
        CFDictionaryAddValue(dict, kSecClass, kSecClassGenericPassword)
        val svc = CFBridgingRetain(service)
        CFDictionaryAddValue(dict, kSecAttrService, svc)
        CFRelease(svc)
        val account = CFBridgingRetain(key)
        CFDictionaryAddValue(dict, kSecAttrAccount, account)
        CFRelease(account)
        extra(dict)
        return dict
    }

    override fun get(key: String): String? = memScoped {
        val q = query(key) {
            CFDictionaryAddValue(it, kSecReturnData, kCFBooleanTrue)
            CFDictionaryAddValue(it, kSecMatchLimit, kSecMatchLimitOne)
        }
        val result = alloc<CFTypeRefVar>()
        val status = SecItemCopyMatching(q, result.ptr)
        CFRelease(q)
        if (status != errSecSuccess) return@memScoped null
        val data = CFBridgingRelease(result.value) as? NSData ?: return@memScoped null
        NSString.create(data = data, encoding = NSUTF8StringEncoding)?.toString()
    }

    override fun put(key: String, value: String) {
        remove(key)
        @Suppress("CAST_NEVER_SUCCEEDS")
        val data = (value as NSString).dataUsingEncoding(NSUTF8StringEncoding) ?: return
        val bridged = CFBridgingRetain(data)
        val q = query(key) {
            CFDictionaryAddValue(it, kSecValueData, bridged)
            CFDictionaryAddValue(it, kSecAttrAccessible, kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly)
        }
        SecItemAdd(q, null)
        CFRelease(q)
        CFRelease(bridged)
    }

    override fun remove(key: String) {
        val q = query(key)
        SecItemDelete(q)
        CFRelease(q)
    }
}
