import React, { useEffect, useRef, useState } from 'react';
import {
    Alert,
    FlatList,
    KeyboardAvoidingView,
    Platform,
    Pressable,
    StyleSheet,
    Text,
    TextInput,
    View,
} from 'react-native';
import CartoonButton from '../components/CartoonButton';
import MessageBubble from '../components/MessageBubble';
import TypingIndicator from '../components/TypingIndicator';
import { cartoonShadow, colors, fonts } from '../constants/theme';
import { parseUTC } from '../utils/dateUtils';
import {
    restoreFirebaseSession,
    signOutFirebase,
} from '../services/firebaseSession';
import { getFirebaseMessages } from '../services/firebaseChatApi';
import { createFirebaseChatConnection } from '../services/firebaseSignalR';

function formatTime(date = new Date()) {
    return date.toLocaleTimeString('vi-VN', {
        hour: '2-digit',
        minute: '2-digit',
    });
}

export default function FirebaseChatRoomScreen({ navigation, route }) {
    const { roomId } = route.params || {};
    const [userId, setUserId] = useState(null);
    const [messages, setMessages] = useState([]);
    const [inputValue, setInputValue] = useState('');
    const [isTyping, setIsTyping] = useState(false);
    const [connected, setConnected] = useState(false);
    const [roomClosed, setRoomClosed] = useState(false);
    const connectionRef = useRef(null);
    const typingTimeoutRef = useRef(null);
    const listRef = useRef(null);
    // true khi user chủ động rời màn hình (LEAVE/logout) -> bỏ qua lỗi
    // 'Invocation canceled' do connection bị stop() giữa lúc JoinRoom đang
    // chờ phản hồi, không phải lỗi thật.
    const leavingRef = useRef(false);


    useEffect(() => {
        let mounted = true;

        const loadFirebaseChat = async () => {
            if (!roomId) {
                Alert.alert('Chat error', 'Room ID is missing.');
                return;
            }

            try {
                // Khôi phục phiên Firebase từ Keychain
                const session = await restoreFirebaseSession();

                if (!mounted) return;

                if (!session) {
                    navigation.replace('Login');
                    return;
                }

                setUserId(session.userId);

                // Lấy 20 tin nhắn mới nhất từ Firestore
                const result = await getFirebaseMessages(
                    roomId,
                    session.token,
                    20,
                );

                if (!mounted) return;

                const formattedMessages = (result.messages ?? [])
                    .slice()
                    .reverse()
                    .map(message => ({
                        id: message.messageId,
                        text: message.content,
                        isOwn: message.senderId === session.userId,
                        timestamp: formatTime(
                            parseUTC(message.createdAt),
                        ),
                    }));

                setMessages(formattedMessages);
            } catch (error) {
                if (mounted) {
                    Alert.alert(
                        'Firebase Chat Error',
                        error.message || 'Cannot load messages.',
                    );
                }
            }
        };

        loadFirebaseChat();

        return () => {
            mounted = false;
        };
    }, [navigation, roomId]);


    useEffect(() => {
        if (!roomId || !userId) return;

        let disposed = false;
        let connection = null;

        const connectFirebaseChat = async () => {
            try {
                connection = createFirebaseChatConnection(async () => {
                    const session = await restoreFirebaseSession();

                    if (!session || session.userId !== userId) {
                        throw new Error('Firebase session is invalid.');
                    }

                    return session.token;
                });

                connection.on('ReceiveMessage', message => {
                    if (disposed) return;

                    setMessages(previous => {
                        if (previous.some(item => item.id === message.messageId)) {
                            return previous;
                        }

                        return [
                            ...previous,
                            {
                                id: message.messageId,
                                text: message.content,
                                isOwn: message.senderId === userId,
                                timestamp: formatTime(
                                    parseUTC(message.createdAt),
                                ),
                            },
                        ];
                    });
                });


                connection.on('PartnerDisconnected', async () => {
                    if (disposed) return;

                    setRoomClosed(true);
                    setConnected(false);

                    Alert.alert(
                        'Conversation ended',
                        'Người kia đã rời phòng. Lịch sử tin nhắn vẫn được giữ lại.',
                    );

                    if (connectionRef.current === connection) {
                        connectionRef.current = null;
                    }

                    await connection.stop().catch(() => { });
                });


                connection.onreconnecting(() => {
                    if (!disposed) setConnected(false);
                });

                connection.onreconnected(async () => {
                    if (disposed) return;

                    try {
                        await connection.invoke('JoinRoom', roomId);

                        if (!disposed) setConnected(true);
                    } catch (error) {
                        console.log('Firebase rejoin failed:', error);
                        if (!disposed) setConnected(false);
                    }
                });

                connection.onclose(() => {
                    if (!disposed) setConnected(false);
                });

                connectionRef.current = connection;

                await connection.start();

                if (disposed) {
                    await connection.stop();
                    return;
                }

                await connection.invoke('JoinRoom', roomId);

                if (!disposed) setConnected(true);
            } catch (error) {
                if (disposed) return;

                setConnected(false);
                Alert.alert(
                    'Firebase SignalR Error',
                    error.message || 'Cannot connect to chat.',
                );

                if (connection) {
                    await connection.stop().catch(() => { });
                }
            }
        };

        connectFirebaseChat();

        return () => {
            disposed = true;

            if (connectionRef.current === connection) {
                connectionRef.current = null;
            }

            connection?.stop().catch(() => { });
        };
    }, [roomId, userId]);



    useEffect(() => {
        setTimeout(() => listRef.current?.scrollToEnd?.({ animated: true }), 100);
    }, [messages, isTyping]);

    const handleChangeText = text => {
        setInputValue(text);
    };


    const handleSend = async () => {
        const message = inputValue.trim();
        const connection = connectionRef.current;

        if (!message) return;

        if (!connected || !connection) {
            Alert.alert(
                'Connection error',
                'Chưa kết nối đến phòng Chat.',
            );
            return;
        }

        try {
            await connection.invoke(
                'SendMessage',
                roomId,
                message,
            );

            setInputValue('');
        } catch (error) {
            Alert.alert(
                'Send failed',
                error.message || 'Không thể gửi tin nhắn.',
            );
        }
    };



    const handleLeave = async () => {
        if (leavingRef.current) return;

        const connection = connectionRef.current;

        if (!connection || !connected) {
            Alert.alert(
                'Connection error',
                'Chưa kết nối đến phòng Chat.',
            );
            return;
        }

        leavingRef.current = true;

        try {
            await connection.invoke('LeaveRoom', roomId);
        } catch (error) {
            leavingRef.current = false;

            Alert.alert(
                'Leave failed',
                error.message || 'Không thể rời phòng Chat.',
            );

            return;
        }

        connectionRef.current = null;
        setConnected(false);

        await connection.stop().catch(() => { });

        if (navigation.canGoBack()) {
            navigation.goBack();
        } else {
            navigation.replace('Login');
        }
    };



    const handleLogout = async () => {
        leavingRef.current = true;

        const connection = connectionRef.current;

        if (connection) {
            try {
                await connection.stop();
            } catch (error) {
                console.warn('SignalR disconnect failed:', error);
            }
        }

        connectionRef.current = null;
        setConnected(false);

        try {
            await signOutFirebase();

            navigation.reset({
                index: 0,
                routes: [{ name: 'Login' }],
            });
        } catch (error) {
            Alert.alert(
                'Logout failed',
                error.message || 'Không thể đăng xuất Firebase.',
            );
        }
    };


    return (
        <KeyboardAvoidingView
            style={styles.container}
            behavior={Platform.OS === 'ios' ? 'padding' : 'height'}
        >
            <View style={styles.header}>
                <View>
                    <Text style={styles.title}>EZone Chat</Text>
                    <Text style={styles.subtitle}>
                        Room #{roomId} • {roomClosed ? 'Closed' : connected ? 'Online' : 'Connecting...'}
                    </Text>
                </View>
                <Pressable onPress={handleLogout} style={styles.logoutButton}>
                    <Text style={styles.logoutText}>Out</Text>
                </Pressable>
            </View>

            <FlatList
                ref={listRef}
                style={styles.list}
                contentContainerStyle={styles.listContent}
                data={messages}
                keyExtractor={item => item.id}
                renderItem={({ item }) => (
                    <MessageBubble message={item.text} isOwn={item.isOwn} timestamp={item.timestamp} />
                )}
                ListFooterComponent={<TypingIndicator visible={isTyping} />}
                ListEmptyComponent={
                    <View style={styles.empty}>
                        <Text style={styles.emptyIcon}>💬</Text>
                        <Text style={styles.emptyText}>Say hi to your mystery buddy!</Text>
                    </View>
                }
            />


            <View style={styles.actions}>
                <CartoonButton
                    title="LEAVE"
                    variant="secondary"
                    onPress={handleLeave}
                    style={styles.actionButton}
                />
            </View>


            <View style={styles.inputRow}>
                <TextInput
                    value={inputValue}
                    onChangeText={handleChangeText}
                    placeholder="Chat here..."
                    placeholderTextColor="#999"
                    style={styles.input}
                    multiline
                />
                <Pressable onPress={handleSend} disabled={!inputValue.trim() || !connected || roomClosed} style={[styles.sendButton, !inputValue.trim() && styles.sendDisabled]}>
                    <Text style={styles.sendText}>➤</Text>
                </Pressable>
            </View>

        </KeyboardAvoidingView>
    );
}

const styles = StyleSheet.create({
    container: {
        flex: 1,
        backgroundColor: colors.background,
    },
    header: {
        ...cartoonShadow,
        backgroundColor: colors.primary,
        paddingTop: Platform.OS === 'android' ? 28 : 10,
        paddingHorizontal: 18,
        paddingBottom: 16,
        flexDirection: 'row',
        justifyContent: 'space-between',
        alignItems: 'center',
    },
    title: {
        color: '#fff',
        fontSize: 24,
        fontFamily: fonts.black, fontWeight: '900',
    },
    subtitle: {
        color: '#ffe3eb',
        fontFamily: fonts.bold, fontWeight: '800',
        marginTop: 3,
    },
    logoutButton: {
        backgroundColor: '#fff',
        borderWidth: 2,
        borderColor: colors.border,
        borderRadius: 12,
        paddingHorizontal: 12,
        paddingVertical: 8,
    },
    logoutText: {
        color: colors.primary,
        fontFamily: fonts.black, fontWeight: '900',
    },
    list: {
        flex: 1,
    },
    listContent: {
        padding: 16,
        paddingBottom: 24,
    },
    empty: {
        alignItems: 'center',
        paddingTop: 80,
    },
    emptyIcon: {
        fontSize: 54,
    },
    emptyText: {
        color: colors.muted,
        fontFamily: fonts.bold, fontWeight: '800',
        marginTop: 10,
    },
    actions: {
        flexDirection: 'row',
        gap: 10,
        paddingHorizontal: 14,
        paddingBottom: 10,
    },
    actionButton: {
        flex: 1,
        minHeight: 46,
    },
    inputRow: {
        ...cartoonShadow,
        flexDirection: 'row',
        alignItems: 'flex-end',
        backgroundColor: colors.card,
        padding: 10,
        margin: 14,
        borderRadius: 16,
    },
    input: {
        flex: 1,
        maxHeight: 110,
        minHeight: 42,
        color: colors.text,
        fontSize: 16,
        fontFamily: fonts.medium, fontWeight: '600',
        paddingHorizontal: 8,
    },
    sendButton: {
        width: 46,
        height: 46,
        borderRadius: 14,
        backgroundColor: colors.primary,
        justifyContent: 'center',
        alignItems: 'center',
        borderWidth: 2,
        borderColor: colors.border,
    },
    sendDisabled: {
        opacity: 0.4,
    },
    sendText: {
        color: '#fff',
        fontSize: 24,
        fontFamily: fonts.black, fontWeight: '900',
    },
});
