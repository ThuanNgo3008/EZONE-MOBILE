import {
    HubConnectionBuilder,
    LogLevel,
} from '@microsoft/signalr';

import { API_BASE_URL } from '../constants/config';

const HUB_URL = `${API_BASE_URL
    .replace(/\/api\/?$/, '')
    .replace(/\/$/, '')}/firebaseChatHub`;

export function createFirebaseChatConnection(getIdToken) {
    if (typeof getIdToken !== 'function') {
        throw new Error('Firebase token provider is required.');
    }

    return new HubConnectionBuilder()
        .withUrl(HUB_URL, {
            accessTokenFactory: async () => {
                const token = await getIdToken();

                if (!token) {
                    throw new Error('Firebase ID Token is missing.');
                }

                return token;
            },
        })
        .withAutomaticReconnect()
        .configureLogging(LogLevel.Warning)
        .build();
}
