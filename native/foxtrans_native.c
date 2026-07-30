#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include "fvad.h"
#include "miniaudio.h"

#if defined(_WIN32)
#define FOXTRANS_EXPORT __declspec(dllexport)
#define FOXTRANS_CALL __cdecl
#else
#define FOXTRANS_EXPORT __attribute__((visibility("default")))
#define FOXTRANS_CALL
#endif

#define FOXTRANS_DEVICE_ID_SIZE 256
#define FOXTRANS_DEVICE_NAME_SIZE 256

#pragma pack(push, 1)
typedef struct foxtrans_audio_device {
    uint8_t id[FOXTRANS_DEVICE_ID_SIZE];
    char name[FOXTRANS_DEVICE_NAME_SIZE];
    int32_t is_default;
} foxtrans_audio_device;
#pragma pack(pop)

typedef void (FOXTRANS_CALL *foxtrans_capture_callback)(
    void *user_data,
    const int16_t *pcm,
    uint32_t frame_count);

typedef struct foxtrans_capture {
    ma_context context;
    ma_device device;
    foxtrans_capture_callback callback;
    void *user_data;
    int context_initialized;
    int device_initialized;
} foxtrans_capture;

_Static_assert(sizeof(ma_device_id) <= FOXTRANS_DEVICE_ID_SIZE,
    "The managed audio-device representation is too small.");

static void foxtrans_data_callback(
    ma_device *device,
    void *output,
    const void *input,
    ma_uint32 frame_count)
{
    (void)output;
    foxtrans_capture *capture = (foxtrans_capture *)device->pUserData;
    if (capture != NULL && capture->callback != NULL && input != NULL) {
        capture->callback(
            capture->user_data,
            (const int16_t *)input,
            (uint32_t)frame_count);
    }
}

static ma_result foxtrans_get_capture_devices(
    ma_context *context,
    ma_device_info **devices,
    ma_uint32 *count)
{
    ma_device_info *playback_devices = NULL;
    ma_uint32 playback_count = 0;
    return ma_context_get_devices(
        context,
        &playback_devices,
        &playback_count,
        devices,
        count);
}

FOXTRANS_EXPORT int FOXTRANS_CALL foxtrans_audio_enumerate(
    foxtrans_audio_device *devices,
    uint32_t capacity,
    uint32_t *total)
{
    if (total == NULL) {
        return MA_INVALID_ARGS;
    }

    ma_context context;
    ma_result result = ma_context_init(NULL, 0, NULL, &context);
    if (result != MA_SUCCESS) {
        return result;
    }

    ma_device_info *capture_devices = NULL;
    ma_uint32 capture_count = 0;
    result = foxtrans_get_capture_devices(
        &context,
        &capture_devices,
        &capture_count);
    if (result == MA_SUCCESS) {
        *total = (uint32_t)capture_count;
        ma_uint32 limit = capture_count < capacity ? capture_count : capacity;
        for (ma_uint32 index = 0; index < limit; index++) {
            memset(&devices[index], 0, sizeof(devices[index]));
            memcpy(
                devices[index].id,
                &capture_devices[index].id,
                sizeof(capture_devices[index].id));
            strncpy(
                devices[index].name,
                capture_devices[index].name,
                FOXTRANS_DEVICE_NAME_SIZE - 1);
            devices[index].is_default = capture_devices[index].isDefault;
        }
    }

    ma_context_uninit(&context);
    return result;
}

FOXTRANS_EXPORT int FOXTRANS_CALL foxtrans_audio_capture_create(
    const uint8_t *device_id,
    int32_t has_device_id,
    foxtrans_capture_callback callback,
    void *user_data,
    foxtrans_capture **created)
{
    if (callback == NULL || created == NULL) {
        return MA_INVALID_ARGS;
    }

    *created = NULL;
    foxtrans_capture *capture = calloc(1, sizeof(*capture));
    if (capture == NULL) {
        return MA_OUT_OF_MEMORY;
    }

    ma_result result = ma_context_init(NULL, 0, NULL, &capture->context);
    if (result != MA_SUCCESS) {
        free(capture);
        return result;
    }
    capture->context_initialized = 1;
    capture->callback = callback;
    capture->user_data = user_data;

    ma_device_config config = ma_device_config_init(ma_device_type_capture);
    config.capture.format = ma_format_s16;
    config.capture.channels = 1;
    config.sampleRate = 16000;
    config.dataCallback = foxtrans_data_callback;
    config.pUserData = capture;

    ma_device_id selected_device;
    if (has_device_id != 0) {
        if (device_id == NULL) {
            ma_context_uninit(&capture->context);
            free(capture);
            return MA_INVALID_ARGS;
        }
        memset(&selected_device, 0, sizeof(selected_device));
        memcpy(&selected_device, device_id, sizeof(selected_device));
        config.capture.pDeviceID = &selected_device;
    }

    result = ma_device_init(&capture->context, &config, &capture->device);
    if (result != MA_SUCCESS) {
        ma_context_uninit(&capture->context);
        free(capture);
        return result;
    }

    capture->device_initialized = 1;
    *created = capture;
    return MA_SUCCESS;
}

FOXTRANS_EXPORT int FOXTRANS_CALL foxtrans_audio_capture_start(
    foxtrans_capture *capture)
{
    return capture == NULL ? MA_INVALID_ARGS : ma_device_start(&capture->device);
}

FOXTRANS_EXPORT int FOXTRANS_CALL foxtrans_audio_capture_stop(
    foxtrans_capture *capture)
{
    return capture == NULL ? MA_INVALID_ARGS : ma_device_stop(&capture->device);
}

FOXTRANS_EXPORT void FOXTRANS_CALL foxtrans_audio_capture_destroy(
    foxtrans_capture *capture)
{
    if (capture == NULL) {
        return;
    }
    if (capture->device_initialized) {
        ma_device_uninit(&capture->device);
    }
    if (capture->context_initialized) {
        ma_context_uninit(&capture->context);
    }
    free(capture);
}

FOXTRANS_EXPORT const char *FOXTRANS_CALL foxtrans_audio_result_description(
    int result)
{
    return ma_result_description((ma_result)result);
}

FOXTRANS_EXPORT void *FOXTRANS_CALL foxtrans_vad_create(void)
{
    return fvad_new();
}

FOXTRANS_EXPORT int FOXTRANS_CALL foxtrans_vad_set_mode(
    void *instance,
    int mode)
{
    return instance == NULL ? -1 : fvad_set_mode((Fvad *)instance, mode);
}

FOXTRANS_EXPORT int FOXTRANS_CALL foxtrans_vad_process(
    void *instance,
    int sample_rate,
    const int16_t *audio_frame,
    uint32_t frame_length)
{
    if (instance == NULL || audio_frame == NULL) {
        return -1;
    }
    if (fvad_set_sample_rate((Fvad *)instance, sample_rate) != 0) {
        return -1;
    }
    return fvad_process((Fvad *)instance, audio_frame, frame_length);
}

FOXTRANS_EXPORT void FOXTRANS_CALL foxtrans_vad_free(void *instance)
{
    if (instance != NULL) {
        fvad_free((Fvad *)instance);
    }
}
