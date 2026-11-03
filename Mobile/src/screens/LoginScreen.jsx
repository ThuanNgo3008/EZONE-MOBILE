import React, { useState, useRef } from 'react';
import { Alert, Pressable, StyleSheet, Text, View } from 'react-native';
import CartoonButton from '../components/CartoonButton';
import InfoCard from '../components/InfoCard';
import Screen from '../components/Screen';
import TextInputField from '../components/TextInputField';
import { colors, fonts } from '../constants/theme';
import { login } from '../services/api';
import { saveSession } from '../services/storage';
import { signInWithFirebase, signOutFirebase } from '../services/firebaseSession';
import { createFirebaseChatConnection } from '../services/firebaseSignalR';

export default function LoginScreen({ navigation }) {
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [loading, setLoading] = useState(false);
  const firebaseConnectionRef = useRef(null);

  const handleLogin = async () => {
    if (!email.trim() || !password.trim()) {
      Alert.alert('Missing information', 'Email and password cannot be empty!');
      return;
    }

    try {
      setLoading(true);
      const data = await login(email.trim(), password);
      await saveSession(data);
      navigation.reset({ index: 0, routes: [{ name: 'Home' }] });
    } catch (error) {
      Alert.alert('Login failed', error.message || 'Invalid email or password');
    } finally {
      setLoading(false);
    }
  };



  const handleTestFirebaseSignalR = async () => {
    if (!email.trim() || !password.trim()) {
      Alert.alert('Thông báo', 'Vui lòng nhập email và mật khẩu.');
      return;
    }

    if (firebaseConnectionRef.current) {
      Alert.alert('Thông báo', 'SignalR đã kết nối.');
      return;
    }

    let connection = null;
    let signedIn = false;

    try {
      setLoading(true);

      const session = await signInWithFirebase(
        email.trim(),
        password,
      );

      signedIn = true;

      connection = createFirebaseChatConnection(
        async () => session.token,
      );

      connection.on('ReceiveMessage', message => {
        console.log('Firebase message:', message);

        Alert.alert(
          'Tin nhắn mới',
          message.content,
        );
      });

      await connection.start();

      const result = await connection.invoke('Ping');

      if (result !== 'pong') {
        throw new Error('SignalR Ping failed.');
      }

      await connection.invoke(
        'JoinRoom',
        'JPCjf6C3lpIZ6A8deXpi',
      );

      firebaseConnectionRef.current = connection;

      Alert.alert(
        'Firebase SignalR',
        'Đã kết nối và tham gia phòng thành công!',
      );
    } catch (error) {
      if (connection) {
        await connection.stop().catch(() => { });
      }

      if (signedIn) {
        await signOutFirebase().catch(() => { });
      }

      Alert.alert('SignalR Failed', error.message);
    } finally {
      setLoading(false);
    }
  };


  const handleSendFirebaseTestMessage = async () => {
    const connection = firebaseConnectionRef.current;

    if (!connection) {
      Alert.alert(
        'Thông báo',
        'Chưa kết nối Firebase SignalR.',
      );
      return;
    }

    try {
      const roomId = 'JPCjf6C3lpIZ6A8deXpi';

      const message = `EZone Test Message ${Date.now()}`;

      await connection.invoke(
        'SendMessage',
        roomId,
        message,
      );

      console.log('Message sent:', message);
    } catch (error) {
      Alert.alert(
        'Send Message Failed',
        error.message,
      );
    }
  };





  return (
    <Screen>
      <View style={styles.center}>
        <View style={styles.head}>
          <Text style={styles.title}>LOGIN</Text>
          <Text style={styles.subtitle}>Step into the Zone.</Text>
        </View>

        <InfoCard style={styles.card}>
          <TextInputField
            label="EIU Email"
            placeholder="example@eiu.edu.vn"
            value={email}
            onChangeText={setEmail}
            keyboardType="email-address"
          />
          <TextInputField
            label="Password"
            placeholder="Your password"
            value={password}
            onChangeText={setPassword}
            secureTextEntry
          />
          <CartoonButton title="GO" onPress={handleLogin} loading={loading} />

          <Pressable
            onPress={handleTestFirebaseSignalR}
            disabled={loading}
          >
            <Text style={styles.link}>
              Test Firebase SignalR
            </Text>
          </Pressable>

          <Pressable onPress={handleSendFirebaseTestMessage}>
            <Text style={styles.link}>
              Send Test Message
            </Text>
          </Pressable>
        </InfoCard>

        <View style={styles.links}>
          <Pressable onPress={() => navigation.navigate('Register')}>
            <Text style={styles.link}>Create account</Text>
          </Pressable>
          <Text style={styles.dot}>•</Text>
          <Pressable onPress={() => navigation.navigate('ActivateAccount')}>
            <Text style={styles.link}>Activate</Text>
          </Pressable>
          <Text style={styles.dot}>•</Text>
          <Pressable onPress={() => navigation.navigate('RulesAbout')}>
            <Text style={styles.link}>EZone Rules</Text>
          </Pressable>
        </View>
      </View>
    </Screen>
  );
}

const styles = StyleSheet.create({
  center: {
    flex: 1,
    justifyContent: 'center',
  },
  head: {
    alignItems: 'center',
  },
  title: {
    fontSize: 38,
    fontFamily: fonts.black, fontWeight: '900',
    color: colors.primary,
    letterSpacing: 2,
  },
  subtitle: {
    color: colors.text,
    fontFamily: fonts.bold, fontWeight: '800',
    marginBottom: 22,
  },
  card: {
    marginBottom: 18,
  },
  links: {
    flexDirection: 'row',
    justifyContent: 'center',
    flexWrap: 'wrap',
    gap: 8,
  },
  link: {
    color: colors.primary,
    fontFamily: fonts.black, fontWeight: '900',
  },
  dot: {
    color: colors.muted,
    fontFamily: fonts.black, fontWeight: '900',
  },
});
