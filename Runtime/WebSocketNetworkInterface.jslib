var LibraryUTPWebSocket = {
    $GlobalData: {
        ws: []
    },
    js_html_utpWebSocketCreate : function(sockId, addrData, addrSize) {
#if USE_PTHREADS
        // On multithreaded builds HEAPU8 is a SharedArrayBuffer, but TextDecoder.decode only
        // accepts ArrayBuffer objects so we need to create a temporary buffer of the right type.
        var subArray = HEAPU8.subarray(addrData, addrData + addrSize);
        var addrBuffer = new Uint8Array(addrSize);
        addrBuffer.set(subArray);
        var addr = new TextDecoder().decode(addrBuffer);
#else
        var addr = new TextDecoder().decode(HEAPU8.subarray(addrData, addrData + addrSize));
#endif
        var sock = new WebSocket(addr);
        sock.binaryType = "arraybuffer";
        sock.utpMessageQueue = [];
        sock.addEventListener('message', function (e) {
            var data8 = new Uint8Array(e.data);
            sock.utpMessageQueue.push(data8);
        });
        GlobalData.ws[sockId] = sock;
    },
    js_html_utpWebSocketDestroy : function(sockId) {
        var sock = GlobalData.ws[sockId];
        if (sock && (sock.readyState == WebSocket.CONNECTING || sock.readyState == WebSocket.OPEN)) {
            sock.close();
        }
        GlobalData.ws[sockId] = undefined;
    },
    js_html_utpWebSocketSend : function(sockId, data, size) {
        var sock = GlobalData.ws[sockId];
        if (!sock || sock.readyState != WebSocket.OPEN)
            return -1;
#if USE_PTHREADS
        // On multithreaded builds HEAPU8 is a SharedArrayBuffer, but the WebSocket send function
        // only accepts ArrayBuffer objects so we need to create a temporary one to pass to send.
        var subArray = HEAPU8.subarray(data, data + size);
        var sendBuffer = new Uint8Array(size);
        sendBuffer.set(subArray);
        sock.send(sendBuffer);
#else
        sock.send(HEAPU8.subarray(data, data + size));
#endif
        return size;
    },
    js_html_utpWebSocketRecv : function(sockId, data, size) {
        var sock = GlobalData.ws[sockId];
        if (!sock || sock.readyState != WebSocket.OPEN)
            return -1;
        if (sock.utpMessageQueue.length == 0)
            return 0;
        var buffer = sock.utpMessageQueue.shift();
        if (buffer.length > size)
            return 0;
        HEAP8.set(buffer, data);
        return buffer.length;
    },
    js_html_utpWebSocketIsConnected : function(sockId) {
        var sock = GlobalData.ws[sockId];
        if (!sock)
            return -1;
        if (sock.readyState == WebSocket.OPEN)
            return 1;
        if (sock.readyState == WebSocket.CONNECTING)
            return 0;
        return -1;
    }
};
autoAddDeps(LibraryUTPWebSocket, '$GlobalData');
mergeInto(LibraryManager.library, LibraryUTPWebSocket);
