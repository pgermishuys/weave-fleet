package io.tryweave.fleet.shared.model

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.longOrNull

/** One Json for the whole app: Fleet adds fields freely, so unknown keys are fine and nulls are left out. */
val FleetJson: Json = Json {
    ignoreUnknownKeys = true
    explicitNulls = false
    coerceInputValues = true
    encodeDefaults = true
    isLenient = true
}

// Loosely typed reads over harness payloads, the Kotlin side of TypeScript's `typeof x === "string"` checks.
val JsonElement?.obj: JsonObject? get() = this as? JsonObject
val JsonElement?.arr: JsonArray? get() = this as? JsonArray
val JsonElement?.string: String? get() = (this as? JsonPrimitive)?.takeIf { it.isString }?.content
val JsonElement?.long: Long? get() = (this as? JsonPrimitive)?.takeIf { !it.isString }?.let { it.longOrNull ?: it.doubleOrNull?.toLong() }
val JsonElement?.double: Double? get() = (this as? JsonPrimitive)?.takeIf { !it.isString }?.doubleOrNull
val JsonElement?.bool: Boolean? get() = (this as? JsonPrimitive)?.takeIf { !it.isString }?.booleanOrNull
val JsonElement?.isNull: Boolean get() = this == null || this is JsonNull

fun JsonObject?.str(key: String): String? = this?.get(key).string
fun JsonObject?.child(key: String): JsonObject? = this?.get(key).obj
fun JsonObject?.num(key: String): Long? = this?.get(key).long

/** A non-empty string, or null. */
fun JsonObject?.text(key: String): String? = str(key)?.takeIf { it.isNotEmpty() }
