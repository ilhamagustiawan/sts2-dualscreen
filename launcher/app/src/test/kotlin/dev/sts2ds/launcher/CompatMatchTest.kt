package dev.sts2ds.launcher

import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class CompatMatchTest {
    private fun target(id: String, sha: Any, vararg versions: String) = JSONObject()
        .put("target_id", id)
        .put("sts2_dll_sha256", sha)
        .put("versions", JSONArray(versions.toList()))

    private fun manifest(vararg targets: JSONObject) =
        JSONObject().put("targets", JSONArray(targets.toList()))

    private fun idOf(match: JSONObject?) = match?.getString("target_id")

    @Test
    fun shaBeatsAnEarlierVersionMatch() {
        val m = manifest(target("by-version", "aaa", "v1"), target("by-sha", "BBB", "v2"))
        assertEquals("by-sha", idOf(CompatMatch.target(m, "bbb", "v1")))
    }

    @Test
    fun shaArraysMatchAnyElement() {
        val m = manifest(target("shared", JSONArray(listOf("aaa", "ccc")), "v1"))
        assertEquals("shared", idOf(CompatMatch.target(m, "ccc", "v9")))
    }

    @Test
    fun versionMatchesWhenNoShaDoes() {
        val m = manifest(target("t", "aaa", "v1", "v2"))
        assertEquals("t", idOf(CompatMatch.target(m, "zzz", "v2")))
    }

    @Test
    fun wildcardTargetsAreRejected() {
        val m = manifest(target("any-version", "aaa", "*"), target("any-sha", "*", "v1"))
        assertNull(CompatMatch.target(m, "aaa", "v1"))
    }

    @Test
    fun noMatchIsNull() {
        assertNull(CompatMatch.target(manifest(target("t", "aaa", "v1")), "zzz", "v2"))
        assertNull(CompatMatch.target(manifest(target("t", "aaa", "v1")), "zzz", ""))
        assertNull(CompatMatch.target(JSONObject(), "aaa", "v1"))
    }
}
