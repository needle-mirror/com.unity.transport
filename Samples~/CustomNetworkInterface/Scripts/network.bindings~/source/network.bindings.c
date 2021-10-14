#include "network.bindings.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#if PLATFORM_WIN
#include <windows.h>
#include <malloc.h>
#include <Mstcpip.h>
#else
#include <unistd.h>
#include <fcntl.h>
#include <sys/socket.h>
#include <sys/time.h>
#define closesocket(x) \
    close(x)
#endif

// TODO: fix this
static int g_initialized;

#if PLATFORM_WIN
const unsigned int SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12;
#endif

static int32_t
native_get_last_error()
{
#if PLATFORM_WIN
    return WSAGetLastError();
#else
    return errno;
#endif
}

static int32_t
native_setsockopt(int64_t handle, int level, int optname, void *optval, int optlen, int32_t* errorcode)
{
    int retval;
#if PLATFORM_WIN
    if ((retval = setsockopt((SOCKET)handle, level, optname, (char*)optval, optlen)) != 0)
#else
    if ((retval = setsockopt((int)handle, level, optname, optval, (socklen_t)optlen)) != 0)
#endif
        *errorcode = native_get_last_error();

    return retval;
}

static int32_t
native_getsockopt(int64_t handle, int level, int optname, void *optval, int optlen, int32_t* errorcode)
{
    int retval;
    // Note: we don't use the return size as we don't have any variable size callers.
#if PLATFORM_WIN
    if ((retval = getsockopt((SOCKET)handle, level, optname, (char*)optval, &optlen)) != 0)
#else
    if ((retval = getsockopt((int)handle, level, optname, optval, (socklen_t*)&optlen)) != 0)
#endif
        *errorcode = native_get_last_error();

    return retval;
}

static int32_t
native_set_blocking(int64_t handle, int blocking, int32_t *errorcode)
{
    int retval;
#if PLATFORM_WIN
    if ((retval = ioctlsocket(handle, FIONBIO, (u_long*)&blocking)) != 0)
#else
    if ((retval = fcntl(handle, F_GETFL, 0)) < 0)
    {
        *errorcode = native_get_last_error();
        return retval;
    }

    if (blocking == 1)
        retval |= O_NONBLOCK;
    else
        retval &= O_NONBLOCK;

    if ((retval = fcntl(handle, F_SETFL, retval)) < 0)
#endif
        *errorcode = native_get_last_error();
    return retval;
}

EXPORT_API int32_t
network_initialize()
{
    int retval = 0;
    if (g_initialized == 0)
    {
#if PLATFORM_WIN
        WSADATA wsaData = {0};
        WSAStartup(MAKEWORD(2, 2), &wsaData);
#endif
    }
    ++g_initialized;
    return retval;
}

EXPORT_API int32_t
network_terminate()
{
    if (g_initialized > 0)
        --g_initialized;

    if (g_initialized == 0)
    {
#if PLATFORM_WIN
        WSACleanup();
#endif
    }
    return SUCCESS;
}

EXPORT_API int32_t
network_set_nonblocking(int64_t handle, int32_t *errorcode)
{
    return native_set_blocking(handle, 1, errorcode);
}

EXPORT_API int32_t
network_set_blocking(int64_t handle, int32_t *errorcode)
{
    return native_set_blocking(handle, 0, errorcode);
}

EXPORT_API int32_t
network_set_send_buffer_size(int64_t handle, int size, int32_t *errorcode)
{
    return native_setsockopt(handle, SOL_SOCKET, SO_SNDBUF, &size, sizeof(size), errorcode);
}

EXPORT_API int32_t
network_get_send_buffer_size(int64_t handle, int *size, int32_t *errorcode)
{
    return native_getsockopt(handle, SOL_SOCKET, SO_SNDBUF, size, sizeof(*size), errorcode);
}

EXPORT_API int32_t
network_set_receive_buffer_size(int64_t handle, int size, int32_t* errorcode)
{
    return native_setsockopt(handle, SOL_SOCKET, SO_RCVBUF, &size, sizeof(size), errorcode);
}

EXPORT_API int32_t
network_get_receive_buffer_size(int64_t handle, int *size, int32_t* errorcode)
{
    return native_getsockopt(handle, SOL_SOCKET, SO_RCVBUF, size, sizeof(*size), errorcode);
}

EXPORT_API int32_t
network_set_receive_timeout(int64_t handle, int64_t timeout_msec, int32_t* errorcode)
{
#if PLATFORM_WIN
    DWORD to = (DWORD)timeout_msec;
#else
    struct timeval to;
    to.tv_sec = (long)(timeout_msec / 1000);
    to.tv_usec = (long)((timeout_msec - (to.tv_sec * 1000)) / 1000000);
#endif
    return native_setsockopt(handle, SOL_SOCKET, SO_RCVTIMEO, &to, sizeof(to), errorcode);
}

EXPORT_API int32_t
network_set_send_timeout(int64_t handle, int64_t timeout_msec, int32_t* errorcode)
{
#if PLATFORM_WIN
    DWORD to = (DWORD)timeout_msec;
#else
    struct timeval to;
    to.tv_sec = (time_t)(timeout_msec / 1000);
    to.tv_usec = (suseconds_t)(timeout_msec - (to.tv_sec * 1000)) / 1000000;
#endif
    return native_setsockopt(handle, SOL_SOCKET, SO_SNDTIMEO, &to, sizeof(to), errorcode);
}

EXPORT_API int32_t
network_set_connection_reset(int64_t handle, int value)
{
    int result = 0;
#if PLATFORM_WIN
    BOOL newValue = (BOOL)value;
    DWORD bytesReturned = 0;
    result = WSAIoctl(handle, SIO_UDP_CONNRESET, &newValue, sizeof(newValue), NULL, 0, &bytesReturned, NULL, NULL);
#else
    // Avoid compiler warnings on non-windows
    (void)handle;
    (void)value;
#endif
    return result;
}

EXPORT_API int32_t network_get_socket_address(int64_t socket_handle, network_address* own_address, int32_t* errorcode)
{
    int retval = 0;
#if PLATFORM_WIN
    if ((retval = getsockname(socket_handle, (struct sockaddr *)own_address, (int *)&own_address->length)) == 0)
#else
    if ((retval = getsockname(socket_handle, (struct sockaddr *)own_address, (socklen_t*)&own_address->length)) < 0)
#endif
        *errorcode = native_get_last_error();
    return retval;
}

EXPORT_API int32_t
network_create_and_bind(int64_t *socket_handle, network_address *address, int32_t* errorcode)
{
    int64_t s = socket(address->addr.sa_family, SOCK_DGRAM, IPPROTO_UDP);
    if (s < 0)
    {
        *errorcode = native_get_last_error();
        return -1;
    }
    if (address->addr.sa_family == AF_INET6)
    {
        int off = 0;
        int result = setsockopt(s, IPPROTO_IPV6, IPV6_V6ONLY, (char *)&off, sizeof(off));
        if (result != 0)
        {
            *errorcode = native_get_last_error();
            closesocket(s);
            return -1;
        }
    }
#if PLATFORM_WIN
    if (bind(s, (const SOCKADDR*)address, (int)address->length) != 0)
#else
    if (bind(s, (const struct sockaddr*)address, (int)address->length) != 0)
#endif
    {
        *errorcode = native_get_last_error();
        closesocket(s);
        return -1;
    }

    *socket_handle = s;
    return 0;
}

EXPORT_API int32_t
network_sendmsg(int64_t socket_handle, network_iov_t *iov, int32_t iov_len, network_address *address, int32_t* errorcode)
{
    int ret = 0;
#if PLATFORM_WIN
    WSABUF *iv = (WSABUF*)alloca(sizeof(WSABUF) * iov_len);
    for (int32_t i = 0; i < iov_len; ++i)
    {
        iv[i].buf = iov[i].data;
        iv[i].len = iov[i].length;
    }

    uint32_t bytes_send;
    ret = WSASendTo(socket_handle, iv, iov_len,
        &bytes_send, 0, (const SOCKADDR *)address,
        address->length, NULL, NULL);
    if (ret != SOCKET_ERROR)
        ret = bytes_send;
#else
    struct iovec *iv = (struct iovec*)alloca(sizeof(struct iovec) * iov_len);
    for (int32_t i = 0; i < iov_len; ++i)
    {
        iv[i].iov_base = iov[i].data;
        iv[i].iov_len = iov[i].length;
    }

    struct msghdr message;
    message.msg_name = (struct sockaddr *)address;
    message.msg_namelen = address->length;
    message.msg_iov = iv;
    message.msg_iovlen = iov_len;
    message.msg_control = NULL;
    message.msg_controllen = 0;
    message.msg_flags = 0;

    ret = sendmsg(socket_handle, (const struct msghdr *)&message, 0);
#endif
    if (ret < 0)
        *errorcode = native_get_last_error();
    return ret;
}

EXPORT_API int32_t
network_recvmsg(int64_t socket_handle, network_iov_t *iov, int32_t iov_len, network_address *remote, int32_t* errorcode)
{
    int ret = 0;

#if PLATFORM_WIN
    WSABUF *iv = (WSABUF*)alloca(sizeof(WSABUF) * iov_len);
    for (int32_t i = 0; i < iov_len; ++i)
    {
        iv[i].buf = iov[i].data;
        iv[i].len = iov[i].length;
    }

    uint32_t flags = 0;
    uint32_t bytes_received = 0;
    ret = WSARecvFrom(socket_handle, iv, iov_len,
        &bytes_received, &flags, (SOCKADDR *)remote,
        &remote->length, NULL, NULL);
    if (ret != SOCKET_ERROR)
        ret = bytes_received;
#else
    struct iovec *iv = (struct iovec*)alloca(sizeof(struct iovec) * iov_len);
    for (int32_t i = 0; i < iov_len; ++i)
    {
        iv[i].iov_base = iov[i].data;
        iv[i].iov_len = iov[i].length;
    }

    struct msghdr message;
    message.msg_name = (struct sockaddr *)remote;
    message.msg_namelen = remote->length;
    message.msg_iov = iv;
    message.msg_iovlen = iov_len;
    message.msg_control = NULL;
    message.msg_controllen = 0;
    message.msg_flags = 0;

    ret = recvmsg(socket_handle, &message, 0);
    remote->length = message.msg_namelen;
    if (message.msg_flags & MSG_TRUNC)
    {
        *errorcode = 10040; // Match the error you would get on windows
        return -1;
    }
#endif
    if (ret < 0)
        *errorcode = native_get_last_error();
    return ret;
}

EXPORT_API int32_t network_close(int64_t *socket_handle, int32_t* errorcode)
{
    int retval = closesocket(*socket_handle);
    if (retval == SOCKET_ERROR)
        *errorcode = native_get_last_error();

    *socket_handle = 0;
    return retval;
}
