// Log levels for libsts2ds. DS_LOG_LEVEL is set per build type by Gradle
// (-DDS_LOG_LEVEL=n, see build.gradle.kts); the values match LogLevel.kt
// and DsLogLevel in DsLog.cs. Lines above the level compile to nothing.
//
//   0 WARN   release  warnings only
//   1 INFO   devel    + milestones and decisions
//   2 DEBUG  debug    + per-event detail
//
// Include after defining TAG.

#ifndef DS_LOG_H
#define DS_LOG_H

#include <android/log.h>

#define DS_LOG_WARN 0
#define DS_LOG_INFO 1
#define DS_LOG_DEBUG 2

#ifndef DS_LOG_LEVEL
#define DS_LOG_LEVEL DS_LOG_WARN
#endif

#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, TAG, __VA_ARGS__)

#if DS_LOG_LEVEL >= DS_LOG_INFO
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, TAG, __VA_ARGS__)
#else
#define LOGI(...) ((void)0)
#endif

#if DS_LOG_LEVEL >= DS_LOG_DEBUG
#define LOGD(...) __android_log_print(ANDROID_LOG_DEBUG, TAG, __VA_ARGS__)
#else
#define LOGD(...) ((void)0)
#endif

#endif
