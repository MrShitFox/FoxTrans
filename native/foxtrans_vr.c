#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#if defined(_WIN32)
#include <windows.h>
#include <tlhelp32.h>
#elif defined(__linux__)
#include <dirent.h>
#endif

#if defined(EXTERN_C)
#undef EXTERN_C
#endif
#include "openvr_capi.h"

#if defined(_WIN32)
#define FOXTRANS_EXPORT __declspec(dllexport)
#define FOXTRANS_CALL __cdecl
#else
#define FOXTRANS_EXPORT __attribute__((visibility("default")))
#define FOXTRANS_CALL
#endif

/* openvr_capi.h intentionally omits global entry points. They are implemented
 * by the vendored loader and have C linkage/cdecl on every supported platform.
 * The overlay interface itself is always accessed through FnTable:. */
extern uint32_t FOXTRANS_CALL VR_InitInternal(
    EVRInitError *error,
    EVRApplicationType application_type);
extern void FOXTRANS_CALL VR_ShutdownInternal(void);
extern void *FOXTRANS_CALL VR_GetGenericInterface(
    const char *version,
    EVRInitError *error);
extern const char *FOXTRANS_CALL VR_GetVRInitErrorAsEnglishDescription(
    EVRInitError error);

enum {
    FOXTRANS_VR_ERROR_INVALID_ARGUMENT = -10001,
    FOXTRANS_VR_ERROR_OUT_OF_MEMORY = -10002,
    FOXTRANS_VR_ERROR_INTERFACE_UNAVAILABLE = -10003,
    FOXTRANS_VR_ERROR_NOT_OPEN = -10004,
    FOXTRANS_VR_ERROR_IMAGE_LOAD_FAILED = -10005
};

typedef struct foxtrans_vr_overlay {
    struct VR_IVROverlay_FnTable *overlay;
    VROverlayHandle_t handle;
    uint8_t *upload_buffer;
    size_t upload_capacity;
    uint8_t *pending_buffer;
    size_t pending_capacity;
    uint32_t pending_width;
    uint32_t pending_height;
    int initialized;
    int created;
    int image_in_flight;
    int pending_image;
    int has_loaded_image;
    int desired_visible;
    int visible;
} foxtrans_vr_overlay;

static int32_t foxtrans_vr_overlay_error(EVROverlayError error)
{
    return (int32_t)error;
}

static int32_t foxtrans_vr_reserve(
    uint8_t **buffer,
    size_t *capacity,
    size_t required)
{
    if (*capacity >= required) {
        return 0;
    }
    uint8_t *resized = realloc(*buffer, required);
    if (resized == NULL) {
        return FOXTRANS_VR_ERROR_OUT_OF_MEMORY;
    }
    *buffer = resized;
    *capacity = required;
    return 0;
}

static int32_t foxtrans_vr_begin_upload(
    foxtrans_vr_overlay *instance,
    uint8_t *buffer,
    uint32_t width,
    uint32_t height)
{
    EVROverlayError error = instance->overlay->SetOverlayRaw(
        instance->handle,
        buffer,
        width,
        height,
        4);
    if (error == EVROverlayError_VROverlayError_None) {
        instance->image_in_flight = 1;
    }
    return foxtrans_vr_overlay_error(error);
}

static int32_t foxtrans_vr_apply_visibility(
    foxtrans_vr_overlay *instance)
{
    if (instance->desired_visible) {
        /* Do not expose an empty overlay while the first raw image is still
         * being loaded by the compositor. */
        if (!instance->has_loaded_image || instance->visible) {
            return 0;
        }
        EVROverlayError error =
            instance->overlay->ShowOverlay(instance->handle);
        if (error == EVROverlayError_VROverlayError_None) {
            instance->visible = 1;
        }
        return foxtrans_vr_overlay_error(error);
    }

    if (!instance->visible) {
        return 0;
    }
    EVROverlayError error =
        instance->overlay->HideOverlay(instance->handle);
    if (error == EVROverlayError_VROverlayError_None) {
        instance->visible = 0;
    }
    return foxtrans_vr_overlay_error(error);
}

/* Do not ask OpenVR whether an HMD is present while SteamVR is closed.
 * VR_IsHmdPresent can load vrclient and boot the runtime on some installations.
 * The supervisor only opens OpenVR after its already-running server is visible.
 */
static int foxtrans_vr_is_steamvr_server_running(void)
{
#if defined(_WIN32)
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) {
        return 0;
    }

    PROCESSENTRY32W process;
    process.dwSize = sizeof(process);
    int running = 0;
    if (Process32FirstW(snapshot, &process)) {
        do {
            if (_wcsicmp(process.szExeFile, L"vrserver.exe") == 0) {
                running = 1;
                break;
            }
        } while (Process32NextW(snapshot, &process));
    }
    CloseHandle(snapshot);
    return running;
#elif defined(__linux__)
    DIR *directory = opendir("/proc");
    if (directory == NULL) {
        return 0;
    }
    int running = 0;
    struct dirent *entry;
    while ((entry = readdir(directory)) != NULL) {
        if (entry->d_name[0] < '0' || entry->d_name[0] > '9') {
            continue;
        }
        char path[256];
        snprintf(path, sizeof(path), "/proc/%s/comm", entry->d_name);
        FILE *file = fopen(path, "r");
        if (file == NULL) {
            continue;
        }
        char name[64] = { 0 };
        if (fgets(name, sizeof(name), file) != NULL) {
            name[strcspn(name, "\r\n")] = '\0';
            if (strcmp(name, "vrserver") == 0) {
                running = 1;
            }
        }
        fclose(file);
        if (running) {
            break;
        }
    }
    closedir(directory);
    return running;
#else
    return 0;
#endif
}

static void foxtrans_vr_build_transform(
    HmdMatrix34_t *matrix,
    float distance_m,
    float pitch_deg,
    float yaw_deg,
    float vertical_m)
{
    const float radians_per_degree = 0.01745329251994329577f;
    float pitch = pitch_deg * radians_per_degree;
    float yaw = yaw_deg * radians_per_degree;
    float cp = cosf(pitch);
    float sp = sinf(pitch);
    float cy = cosf(yaw);
    float sy = sinf(yaw);

    /* SteamVR's local HMD space is +Y up and -Z forward. Compose yaw then
     * pitch and keep translation HMD-relative. Smooth-follow can change only
     * this construction/site to use an absolute transform later. */
    matrix->m[0][0] = cy;
    matrix->m[0][1] = sy * sp;
    matrix->m[0][2] = sy * cp;
    matrix->m[0][3] = 0.0f;
    matrix->m[1][0] = 0.0f;
    matrix->m[1][1] = cp;
    matrix->m[1][2] = -sp;
    matrix->m[1][3] = vertical_m;
    matrix->m[2][0] = -sy;
    matrix->m[2][1] = cy * sp;
    matrix->m[2][2] = cy * cp;
    matrix->m[2][3] = -distance_m;
}

FOXTRANS_EXPORT int32_t FOXTRANS_CALL foxtrans_vr_is_available(void)
{
    return foxtrans_vr_is_steamvr_server_running();
}

FOXTRANS_EXPORT int32_t FOXTRANS_CALL foxtrans_vr_open(
    const char *key,
    const char *name,
    foxtrans_vr_overlay **created)
{
    if (key == NULL || name == NULL || created == NULL) {
        return FOXTRANS_VR_ERROR_INVALID_ARGUMENT;
    }

    *created = NULL;
    foxtrans_vr_overlay *instance = calloc(1, sizeof(*instance));
    if (instance == NULL) {
        return FOXTRANS_VR_ERROR_OUT_OF_MEMORY;
    }

    EVRInitError init_error = EVRInitError_VRInitError_None;
    VR_InitInternal(&init_error, EVRApplicationType_VRApplication_Overlay);
    if (init_error != EVRInitError_VRInitError_None) {
        free(instance);
        return (int32_t)init_error;
    }
    instance->initialized = 1;

    char interface_name[64];
    int interface_length = snprintf(
        interface_name,
        sizeof(interface_name),
        "FnTable:%s",
        IVROverlay_Version);
    if (interface_length < 0 || (size_t)interface_length >= sizeof(interface_name)) {
        VR_ShutdownInternal();
        free(instance);
        return FOXTRANS_VR_ERROR_INTERFACE_UNAVAILABLE;
    }
    instance->overlay = (struct VR_IVROverlay_FnTable *)VR_GetGenericInterface(
        interface_name,
        &init_error);
    if (init_error != EVRInitError_VRInitError_None || instance->overlay == NULL) {
        VR_ShutdownInternal();
        free(instance);
        return init_error == EVRInitError_VRInitError_None
            ? FOXTRANS_VR_ERROR_INTERFACE_UNAVAILABLE
            : (int32_t)init_error;
    }

    EVROverlayError overlay_error = instance->overlay->CreateOverlay(
        (char *)key,
        (char *)name,
        &instance->handle);
    if (overlay_error != EVROverlayError_VROverlayError_None) {
        VR_ShutdownInternal();
        free(instance);
        return foxtrans_vr_overlay_error(overlay_error);
    }

    instance->created = 1;
    *created = instance;
    return 0;
}

FOXTRANS_EXPORT int32_t FOXTRANS_CALL foxtrans_vr_submit(
    foxtrans_vr_overlay *instance,
    const uint8_t *rgba,
    uint32_t width,
    uint32_t height)
{
    if (instance == NULL || instance->overlay == NULL || !instance->created) {
        return FOXTRANS_VR_ERROR_NOT_OPEN;
    }
    if (rgba == NULL || width == 0 || height == 0) {
        return FOXTRANS_VR_ERROR_INVALID_ARGUMENT;
    }
    if ((size_t)width > SIZE_MAX / (size_t)height ||
        (size_t)width * (size_t)height > SIZE_MAX / 4) {
        return FOXTRANS_VR_ERROR_INVALID_ARGUMENT;
    }

    size_t byte_count = (size_t)width * (size_t)height * 4;
    if (instance->image_in_flight) {
        int32_t reserve = foxtrans_vr_reserve(
            &instance->pending_buffer,
            &instance->pending_capacity,
            byte_count);
        if (reserve != 0) {
            return reserve;
        }
        memcpy(instance->pending_buffer, rgba, byte_count);
        instance->pending_width = width;
        instance->pending_height = height;
        instance->pending_image = 1;
        return 0;
    }

    int32_t reserve = foxtrans_vr_reserve(
        &instance->upload_buffer,
        &instance->upload_capacity,
        byte_count);
    if (reserve != 0) {
        return reserve;
    }
    memcpy(instance->upload_buffer, rgba, byte_count);
    return foxtrans_vr_begin_upload(
        instance,
        instance->upload_buffer,
        width,
        height);
}

FOXTRANS_EXPORT int32_t FOXTRANS_CALL foxtrans_vr_set_placement(
    foxtrans_vr_overlay *instance,
    float width_m,
    float distance_m,
    float pitch_deg,
    float yaw_deg,
    float vertical_m)
{
    if (instance == NULL || instance->overlay == NULL || !instance->created) {
        return FOXTRANS_VR_ERROR_NOT_OPEN;
    }
    if (width_m <= 0.0f || distance_m <= 0.0f) {
        return FOXTRANS_VR_ERROR_INVALID_ARGUMENT;
    }

    EVROverlayError error = instance->overlay->SetOverlayWidthInMeters(
        instance->handle,
        width_m);
    if (error != EVROverlayError_VROverlayError_None) {
        return foxtrans_vr_overlay_error(error);
    }

    HmdMatrix34_t matrix;
    foxtrans_vr_build_transform(
        &matrix,
        distance_m,
        pitch_deg,
        yaw_deg,
        vertical_m);
    return foxtrans_vr_overlay_error(
        instance->overlay->SetOverlayTransformTrackedDeviceRelative(
            instance->handle,
            k_unTrackedDeviceIndex_Hmd,
            &matrix));
}

FOXTRANS_EXPORT int32_t FOXTRANS_CALL foxtrans_vr_set_opacity(
    foxtrans_vr_overlay *instance,
    float alpha)
{
    if (instance == NULL || instance->overlay == NULL || !instance->created) {
        return FOXTRANS_VR_ERROR_NOT_OPEN;
    }
    if (alpha < 0.0f || alpha > 1.0f) {
        return FOXTRANS_VR_ERROR_INVALID_ARGUMENT;
    }
    return foxtrans_vr_overlay_error(instance->overlay->SetOverlayAlpha(
        instance->handle,
        alpha));
}

FOXTRANS_EXPORT int32_t FOXTRANS_CALL foxtrans_vr_set_visible(
    foxtrans_vr_overlay *instance,
    int32_t visible)
{
    if (instance == NULL || instance->overlay == NULL || !instance->created) {
        return FOXTRANS_VR_ERROR_NOT_OPEN;
    }
    instance->desired_visible = visible != 0;
    return foxtrans_vr_apply_visibility(instance);
}

FOXTRANS_EXPORT int32_t FOXTRANS_CALL foxtrans_vr_poll(
    foxtrans_vr_overlay *instance,
    int32_t *should_quit)
{
    if (should_quit == NULL) {
        return FOXTRANS_VR_ERROR_INVALID_ARGUMENT;
    }
    *should_quit = 0;
    if (instance == NULL || instance->overlay == NULL || !instance->created) {
        return FOXTRANS_VR_ERROR_NOT_OPEN;
    }

    int32_t result = 0;
    struct VREvent_t event;
    while (instance->overlay->PollNextOverlayEvent(
        instance->handle,
        &event,
        (uint32_t)sizeof(event))) {
        if (event.eventType == EVREventType_VREvent_Quit) {
            *should_quit = 1;
            break;
        }
        if (event.eventType == EVREventType_VREvent_ImageFailed) {
            instance->image_in_flight = 0;
            instance->pending_image = 0;
            return FOXTRANS_VR_ERROR_IMAGE_LOAD_FAILED;
        }
        if (event.eventType != EVREventType_VREvent_ImageLoaded) {
            continue;
        }

        instance->image_in_flight = 0;
        instance->has_loaded_image = 1;
        result = foxtrans_vr_apply_visibility(instance);
        if (result != 0) {
            return result;
        }
        if (instance->pending_image) {
            uint8_t *previous_upload = instance->upload_buffer;
            size_t previous_capacity = instance->upload_capacity;
            instance->upload_buffer = instance->pending_buffer;
            instance->upload_capacity = instance->pending_capacity;
            instance->pending_buffer = previous_upload;
            instance->pending_capacity = previous_capacity;
            instance->pending_image = 0;
            result = foxtrans_vr_begin_upload(
                instance,
                instance->upload_buffer,
                instance->pending_width,
                instance->pending_height);
            if (result != 0) {
                return result;
            }
        }
    }
    return 0;
}

FOXTRANS_EXPORT void FOXTRANS_CALL foxtrans_vr_close(
    foxtrans_vr_overlay *instance)
{
    if (instance == NULL) {
        return;
    }
    if (instance->created && instance->overlay != NULL) {
        instance->overlay->DestroyOverlay(instance->handle);
        instance->created = 0;
    }
    if (instance->initialized) {
        VR_ShutdownInternal();
        instance->initialized = 0;
    }
    free(instance->upload_buffer);
    free(instance->pending_buffer);
    free(instance);
}

FOXTRANS_EXPORT const char *FOXTRANS_CALL foxtrans_vr_result_description(
    int32_t code)
{
    switch (code) {
    case 0:
        return "Success";
    case FOXTRANS_VR_ERROR_INVALID_ARGUMENT:
        return "Invalid VR overlay argument";
    case FOXTRANS_VR_ERROR_OUT_OF_MEMORY:
        return "VR overlay allocation failed";
    case FOXTRANS_VR_ERROR_INTERFACE_UNAVAILABLE:
        return "SteamVR overlay interface is unavailable";
    case FOXTRANS_VR_ERROR_NOT_OPEN:
        return "SteamVR overlay is not open";
    case FOXTRANS_VR_ERROR_IMAGE_LOAD_FAILED:
        return "SteamVR failed to load the overlay image";
    default:
        return VR_GetVRInitErrorAsEnglishDescription((EVRInitError)code);
    }
}
