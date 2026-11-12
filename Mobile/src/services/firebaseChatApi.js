import { API_BASE_URL } from '../constants/config';

export async function getFirebaseMessages(
    roomId,
    idToken,
    pageSize = 20,
    cursor = null,
) {
    if (!roomId || !idToken) {
        throw new Error('Room ID and Firebase token are required.');
    }

    const params = new URLSearchParams({
        pageSize: String(pageSize),
    });

    if (cursor) {
        params.append('cursor', cursor);
    }

    const url =
        `${API_BASE_URL}/firebase-chat-messages/` +
        `${encodeURIComponent(roomId)}?${params.toString()}`;

    const response = await fetch(url, {
        method: 'GET',
        headers: {
            Authorization: `Bearer ${idToken}`,
        },
    });

    if (!response.ok) {
        throw new Error(
            `Failed to load Firebase messages (${response.status}).`,
        );
    }

    return response.json();
}
