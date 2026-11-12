import * as Keychain from 'react-native-keychain';

const FIREBASE_SERVICE = 'ezone.firebase.session';

export async function saveFirebaseSession(refreshToken) {
    if (!refreshToken) {
        throw new Error('Firebase refresh token is required.');
    }

    await Keychain.setGenericPassword(
        'firebase',
        refreshToken,
        { service: FIREBASE_SERVICE },
    );
}

export async function getFirebaseRefreshToken() {
    const credentials = await Keychain.getGenericPassword({
        service: FIREBASE_SERVICE,
    });

    return credentials ? credentials.password : null;
}

export async function clearFirebaseSession() {
    await Keychain.resetGenericPassword({
        service: FIREBASE_SERVICE,
    });
}
