import React, { useState, useEffect, useCallback, useRef } from 'react';
import {
  SafeAreaView,
  View,
  Text,
  FlatList,
  Pressable,
  StyleSheet,
  Modal,
  ActivityIndicator,
  Alert,
} from 'react-native';
import { useFocusEffect } from '@react-navigation/native';
import { fonts } from '../constants/theme';
import CartoonButton from '../components/CartoonButton';
import { getAdminReports, banReportedUser, deleteReport } from '../services/api';
import { formatDateShort as formatDate } from '../utils/dateUtils';

const COLORS = {
  border: '#111111',
  cardBg: '#FFFFFF',
  shadow: '#111111',
  primary: '#ED2553',
  textPrimary: '#111111',
  textMuted: '#6B7280',
  background: '#F7F5F5',
  danger: '#B91C1C',
  overlay: 'rgba(0,0,0,0.55)',
};

const STATUS_OPTIONS = [
  { value: '', label: 'All Status' },
  { value: 'Pending', label: 'Pending' },
  { value: 'Resolved', label: 'Resolved' },
];

const SORT_ORDER_OPTIONS = [
  { value: 'desc', label: 'Descending' },
  { value: 'asc', label: 'Ascending' },
];

const PAGE_SIZE = 5;

const AdminReportListScreen = ({ navigation }) => {
  const [reports, setReports] = useState([]);
  const [isLoading, setIsLoading] = useState(false);

  const [statusFilter, setStatusFilter] = useState('');
  const [sortOrder, setSortOrder] = useState('desc');

  const [currentPage, setCurrentPage] = useState(1);
  const [totalPages, setTotalPages] = useState(1);
  const [totalItems, setTotalItems] = useState(0);

  const [showFilterSheet, setShowFilterSheet] = useState(false);

  const [banTarget, setBanTarget] = useState(null);
  const [deleteTarget, setDeleteTarget] = useState(null);
  const [processing, setProcessing] = useState(false);

  const loadReports = async (page = 1) => {
    setIsLoading(true);

    try {
      const result = await getAdminReports({
        page,
        pageSize: PAGE_SIZE,
        status: statusFilter,
        sortOrder,
      });

      setReports(result.data);
      setTotalPages(result.totalPages || 1);
      setTotalItems(result.totalItems || 0);
      setCurrentPage(page);
    } catch (error) {
      Alert.alert('Load reports failed', error.message || 'Please try again.');
    } finally {
      setIsLoading(false);
    }
  };

  useEffect(() => {
    loadReports(1);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [statusFilter, sortOrder]);

  // Refetch trang hiện tại khi quay lại từ AdminReportDetailScreen (sau khi đổi
  // status) — bỏ qua lần focus đầu tiên vì useEffect ở trên đã tự load rồi.
  const isFirstFocus = useRef(true);
  useFocusEffect(
    useCallback(() => {
      if (isFirstFocus.current) {
        isFirstFocus.current = false;
        return;
      }
      loadReports(currentPage);
      // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [currentPage])
  );

  const handleApplyFilter = (next) => {
    if (next.status !== undefined) setStatusFilter(next.status);
    if (next.sortOrder !== undefined) setSortOrder(next.sortOrder);
    setShowFilterSheet(false);
  };

  const handlePrevPage = () => {
    if (currentPage > 1) loadReports(currentPage - 1);
  };

  const handleNextPage = () => {
    if (currentPage < totalPages) loadReports(currentPage + 1);
  };

  const handleConfirmBan = async () => {
    setProcessing(true);
    try {
      await banReportedUser(banTarget.reportId);
      setBanTarget(null);
      await loadReports(currentPage);
    } catch (error) {
      Alert.alert('Ban failed', error.message || 'Please try again.');
    } finally {
      setProcessing(false);
    }
  };

  const handleConfirmDelete = async () => {
    setProcessing(true);
    try {
      await deleteReport(deleteTarget.reportId);
      setDeleteTarget(null);
      await loadReports(currentPage);
    } catch (error) {
      Alert.alert('Delete failed', error.message || 'Please try again.');
    } finally {
      setProcessing(false);
    }
  };


  return (
    <SafeAreaView style={styles.safeArea}>
      {/* ---- Header ---- */}
      <View style={styles.header}>
        <Text style={styles.headerTitle}>Chat Reports</Text>
        <Pressable style={styles.filterButton} onPress={() => setShowFilterSheet(true)}>
          <Text style={styles.filterButtonText}>Filter ⌄</Text>
        </Pressable>
      </View>

      {/* ---- Active filter summary ---- */}
      <View style={styles.filterSummaryRow}>
        <Text style={styles.filterSummaryText}>
          {STATUS_OPTIONS.find((s) => s.value === statusFilter)?.label} ·{' '}
          {SORT_ORDER_OPTIONS.find((s) => s.value === sortOrder)?.label}
        </Text>
      </View>

      {/* ---- List ---- */}
      {isLoading ? (
        <View style={styles.loadingBox}>
          <ActivityIndicator size="large" color={COLORS.primary} />
        </View>
      ) : (
        <FlatList
          data={reports}
          keyExtractor={(item) => String(item.reportId)}
          contentContainerStyle={styles.listContent}
          renderItem={({ item }) => (
            <View style={styles.cardWrapper}>
              <View style={styles.shadowLayer} />
              <View style={styles.card}>
                <View style={styles.cardTopRow}>
                  <Text style={styles.reportCode}>#{item.reportId}</Text>
                  <View
                    style={[
                      styles.statusBadge,
                      item.status === 'Pending' ? styles.statusPending : styles.statusResolved,
                    ]}
                  >
                    <Text style={styles.statusBadgeText}>{item.status}</Text>
                  </View>
                </View>

                <Text style={styles.reasonText}>{item.reason}</Text>
                <Text style={styles.messageText} numberOfLines={2}>
                  {item.violatingMessage}
                </Text>

                <View style={styles.metaRow}>
                  <Text style={styles.metaText}>Room #{item.roomId}</Text>
                  <Text style={styles.metaText}>
                    Reporter #{item.reporterId} → Reported #{item.reportedUserId}
                  </Text>
                </View>
                <Text style={styles.dateText}>{formatDate(item.createdAt)}</Text>

                {/* Ban source badge */}
                {item.reportedUserIsBanned && (
                  <View style={styles.banBadge}>
                    <Text style={styles.banBadgeText}>
                      {item.reportedUserBanSource === 'Admin'
                        ? '🔨 Banned by Admin'
                        : item.reportedUserBanSource === 'SystemAuto'
                        ? '🤖 Auto-banned (System)'
                        : '🚫 Banned'}
                    </Text>
                  </View>
                )}

                <Pressable
                  style={styles.viewDetailButton}
                  onPress={() => navigation.navigate('AdminReportDetail', { report: item })}
                >
                  <Text style={styles.viewDetailText}>View / Update Status ›</Text>
                </Pressable>

                <View style={styles.actionRow}>
                  <Pressable
                    style={[
                      styles.actionButton,
                      styles.banButton,
                      item.reportedUserIsBanned && styles.banButtonDisabled,
                    ]}
                    onPress={() => setBanTarget(item)}
                    disabled={item.reportedUserIsBanned}
                  >
                    <Text style={styles.banButtonText}>
                      {item.reportedUserIsBanned ? 'Banned' : 'Ban User'}
                    </Text>
                  </Pressable>
                  <Pressable
                    style={[styles.actionButton, styles.deleteButton]}
                    onPress={() => setDeleteTarget(item)}
                  >
                    <Text style={styles.deleteButtonText}>Delete</Text>
                  </Pressable>
                </View>
              </View>
            </View>
          )}
          ListEmptyComponent={
            <Text style={styles.emptyText}>No reports match this filter.</Text>
          }
          ListFooterComponent={
            totalItems > 0 ? (
              <View style={styles.paginationRow}>
                <Pressable
                  style={[styles.pageButton, currentPage <= 1 && styles.pageButtonDisabled]}
                  onPress={handlePrevPage}
                  disabled={currentPage <= 1}
                >
                  <Text style={styles.pageButtonText}>‹ Prev</Text>
                </Pressable>

                <Text style={styles.pageIndicator}>
                  Page {currentPage}/{totalPages} · {totalItems} reports
                </Text>

                <Pressable
                  style={[styles.pageButton, currentPage >= totalPages && styles.pageButtonDisabled]}
                  onPress={handleNextPage}
                  disabled={currentPage >= totalPages}
                >
                  <Text style={styles.pageButtonText}>Next ›</Text>
                </Pressable>
              </View>
            ) : null
          }
        />
      )}

      <View style={styles.backButtonWrap}>
        <CartoonButton title="BACK" variant="secondary" onPress={() => navigation.goBack()} />
      </View>

      {/* ================= Filter Bottom Sheet ================= */}
      <Modal
        visible={showFilterSheet}
        transparent
        animationType="slide"
        onRequestClose={() => setShowFilterSheet(false)}
      >
        <Pressable style={styles.sheetOverlay} onPress={() => setShowFilterSheet(false)}>
          <Pressable style={styles.sheet} onPress={(e) => e.stopPropagation?.()}>
            <Text style={styles.sheetTitle}>Filter & Sort</Text>

            {/* Mục Status */}
            <Text style={styles.sheetGroupLabel}>Status</Text>
            <View style={styles.chipRow}>
              {STATUS_OPTIONS.map((opt) => (
                <Pressable
                  key={opt.value}
                  style={[styles.chip, statusFilter === opt.value && styles.chipActive]}
                  onPress={() => handleApplyFilter({ status: opt.value })}
                >
                  <Text
                    style={[
                      styles.chipText,
                      statusFilter === opt.value && styles.chipTextActive,
                    ]}
                  >
                    {opt.label}
                  </Text>
                </Pressable>
              ))}
            </View>

            {/* Mục Sort Order */}
            <Text style={styles.sheetGroupLabel}>Sort Order</Text>
            <View style={styles.chipRow}>
              {SORT_ORDER_OPTIONS.map((opt) => (
                <Pressable
                  key={opt.value}
                  style={[styles.chip, sortOrder === opt.value && styles.chipActive]}
                  onPress={() => handleApplyFilter({ sortOrder: opt.value })}
                >
                  <Text
                    style={[
                      styles.chipText,
                      sortOrder === opt.value && styles.chipTextActive,
                    ]}
                  >
                    {opt.label}
                  </Text>
                </Pressable>
              ))}
            </View>
          </Pressable>
        </Pressable>
      </Modal>

      {/* ================= Ban User Confirm Dialog ================= */}
      <Modal
        visible={!!banTarget}
        transparent
        animationType="fade"
        onRequestClose={() => setBanTarget(null)}
        statusBarTranslucent
      >
        <View style={styles.dialogOverlay}>
          <Pressable
            style={StyleSheet.absoluteFill}
            onPress={processing ? undefined : () => setBanTarget(null)}
          />
          <View style={styles.dialogCardWrapper}>
            <View style={styles.shadowLayer} />
            <View style={styles.dialogCard}>
              <Text style={styles.dialogTitle}>Ban this user?</Text>
              <Text style={styles.dialogDescription}>
                User #{banTarget?.reportedUserId} will be permanently banned from EZone
                based on report #{banTarget?.reportId}. This action cannot be undone.
              </Text>

              <View style={styles.dialogButtonRow}>
                <Pressable
                  onPress={() => setBanTarget(null)}
                  disabled={processing}
                  style={[styles.dialogButton, styles.dialogNeutralButton]}
                >
                  <Text style={styles.dialogNeutralText}>Cancel</Text>
                </Pressable>
                <Pressable
                  onPress={handleConfirmBan}
                  disabled={processing}
                  style={[styles.dialogButton, styles.dialogDangerButton]}
                >
                  {processing ? (
                    <ActivityIndicator size="small" color="#FFFFFF" />
                  ) : (
                    <Text style={styles.dialogDangerText}>Ban User</Text>
                  )}
                </Pressable>
              </View>
            </View>
          </View>
        </View>
      </Modal>

      {/* ================= Delete Report Confirm Dialog ================= */}
      <Modal
        visible={!!deleteTarget}
        transparent
        animationType="fade"
        onRequestClose={() => setDeleteTarget(null)}
        statusBarTranslucent
      >
        <View style={styles.dialogOverlay}>
          <Pressable
            style={StyleSheet.absoluteFill}
            onPress={processing ? undefined : () => setDeleteTarget(null)}
          />
          <View style={styles.dialogCardWrapper}>
            <View style={styles.shadowLayer} />
            <View style={styles.dialogCard}>
              <Text style={styles.dialogTitle}>Delete this report?</Text>
              <Text style={styles.dialogDescription}>
                Report #{deleteTarget?.reportId} will be permanently removed. This
                action cannot be undone.
              </Text>

              <View style={styles.dialogButtonRow}>
                <Pressable
                  onPress={() => setDeleteTarget(null)}
                  disabled={processing}
                  style={[styles.dialogButton, styles.dialogNeutralButton]}
                >
                  <Text style={styles.dialogNeutralText}>Cancel</Text>
                </Pressable>
                <Pressable
                  onPress={handleConfirmDelete}
                  disabled={processing}
                  style={[styles.dialogButton, styles.dialogDangerButton]}
                >
                  {processing ? (
                    <ActivityIndicator size="small" color="#FFFFFF" />
                  ) : (
                    <Text style={styles.dialogDangerText}>Delete</Text>
                  )}
                </Pressable>
              </View>
            </View>
          </View>
        </View>
      </Modal>
    </SafeAreaView>
  );
};

const styles = StyleSheet.create({
  safeArea: { flex: 1, backgroundColor: COLORS.background },
  header: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', paddingHorizontal: 20, paddingTop: 16, paddingBottom: 8, marginTop: 50 },
  headerTitle: { fontSize: 22, fontFamily: fonts.bold, fontWeight: '800', color: COLORS.textPrimary },
  filterButton: { marginTop: 10, borderWidth: 2, borderColor: COLORS.border, borderRadius: 10, paddingHorizontal: 12, paddingVertical: 7, backgroundColor: '#FFFFFF' },
  filterButtonText: { fontSize: 12, fontFamily: fonts.bold, fontWeight: '700', color: COLORS.textPrimary },
  filterSummaryRow: { paddingHorizontal: 20, marginBottom: 8 },
  filterSummaryText: { fontSize: 11, color: COLORS.textMuted, fontFamily: fonts.medium, fontWeight: '500' },
  loadingBox: { flex: 1, alignItems: 'center', justifyContent: 'center' },
  listContent: { paddingHorizontal: 20, paddingBottom: 12 },
  cardWrapper: { marginBottom: 16 },
  shadowLayer: { position: 'absolute', top: 5, left: 5, right: -5, bottom: -5, backgroundColor: COLORS.shadow, borderRadius: 16 },
  card: { backgroundColor: COLORS.cardBg, borderRadius: 16, borderWidth: 2.5, borderColor: COLORS.border, padding: 16 },
  cardTopRow: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', marginBottom: 8 },
  reportCode: { fontSize: 13, fontFamily: fonts.bold, fontWeight: '800', color: COLORS.textPrimary },
  statusBadge: { paddingHorizontal: 10, paddingVertical: 4, borderRadius: 999, borderWidth: 1.5, borderColor: COLORS.border },
  statusPending: { backgroundColor: '#FEF3C7' },
  statusResolved: { backgroundColor: '#DCFCE7' },
  statusBadgeText: { fontSize: 10, fontFamily: fonts.bold, fontWeight: '800', color: COLORS.textPrimary },
  reasonText: { fontSize: 13, fontFamily: fonts.bold, fontWeight: '700', color: COLORS.primary, textTransform: 'capitalize', marginBottom: 4 },
  messageText: { fontSize: 13, color: COLORS.textPrimary, lineHeight: 19, marginBottom: 10 },
  metaRow: { marginBottom: 4 },
  metaText: { fontSize: 11, color: COLORS.textMuted, fontFamily: fonts.medium, fontWeight: '600' },
  dateText: { fontSize: 11, color: COLORS.textMuted, marginBottom: 8 },
  viewDetailButton: { marginBottom: 10 },
  viewDetailText: { fontSize: 12, fontFamily: fonts.bold, fontWeight: '700', color: COLORS.primary },
  actionRow: { flexDirection: 'row', gap: 8 },
  actionButton: { flex: 1, borderWidth: 2, borderColor: COLORS.border, borderRadius: 10, paddingVertical: 9, alignItems: 'center' },
  banButton: { backgroundColor: '#ED2553' },
  banButtonText: { color: '#FFFFFF', fontFamily: fonts.bold, fontWeight: '700', fontSize: 12 },
  banButtonDisabled: { backgroundColor: '#9CA3AF' },
  banBadge: { backgroundColor: '#FEF3C7', borderRadius: 8, paddingHorizontal: 10, paddingVertical: 5, marginBottom: 8, alignSelf: 'flex-start', borderWidth: 1, borderColor: '#F59E0B' },
  banBadgeText: { fontSize: 11, fontFamily: fonts.bold, fontWeight: '700', color: '#92400E' },
  deleteButton: { backgroundColor: '#FFFFFF' },
  deleteButtonText: { color: COLORS.danger, fontFamily: fonts.bold, fontWeight: '700', fontSize: 12 },
  paginationRow: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', marginTop: 8, marginBottom: 16 },
  pageButton: { borderWidth: 2, borderColor: COLORS.border, borderRadius: 10, paddingHorizontal: 14, paddingVertical: 8, backgroundColor: '#FFFFFF' },
  pageButtonDisabled: { opacity: 0.4 },
  pageButtonText: { fontSize: 12, fontFamily: fonts.bold, fontWeight: '700', color: COLORS.textPrimary },
  pageIndicator: { fontSize: 11, color: COLORS.textMuted, fontFamily: fonts.medium, fontWeight: '600' },
  backButtonWrap: { paddingHorizontal: 20, paddingBottom: 16 },
  emptyText: { textAlign: 'center', color: COLORS.textMuted, marginTop: 40, fontFamily: fonts.medium, fontWeight: '500' },
  sheetOverlay: { flex: 1, backgroundColor: COLORS.overlay, justifyContent: 'flex-end' },
  sheet: { backgroundColor: '#FFFFFF', borderTopLeftRadius: 20, borderTopRightRadius: 20, borderWidth: 2.5, borderColor: COLORS.border, borderBottomWidth: 0, padding: 20, paddingBottom: 32 },
  sheetTitle: { fontSize: 17, fontFamily: fonts.bold, fontWeight: '800', color: COLORS.textPrimary, marginBottom: 16 },
  sheetGroupLabel: { fontSize: 12, fontFamily: fonts.bold, fontWeight: '700', color: COLORS.textMuted, textTransform: 'uppercase', marginBottom: 8, marginTop: 12 },
  chipRow: { flexDirection: 'row', flexWrap: 'wrap', gap: 8 },
  chip: { borderWidth: 2, borderColor: COLORS.border, borderRadius: 999, paddingHorizontal: 14, paddingVertical: 8, backgroundColor: '#FFFFFF' },
  chipActive: { backgroundColor: COLORS.primary },
  chipText: { fontSize: 12, fontFamily: fonts.bold, fontWeight: '700', color: COLORS.textPrimary },
  chipTextActive: { color: '#FFFFFF' },
  dialogOverlay: { flex: 1, backgroundColor: COLORS.overlay, alignItems: 'center', justifyContent: 'center', paddingHorizontal: 24 },
  dialogCardWrapper: { width: '100%', maxWidth: 400 },
  dialogCard: { backgroundColor: COLORS.cardBg, borderRadius: 20, borderWidth: 2.5, borderColor: COLORS.border, paddingTop: 24, paddingBottom: 22, paddingHorizontal: 22 },
  dialogTitle: { fontSize: 18, fontFamily: fonts.bold, fontWeight: '800', color: COLORS.textPrimary },
  dialogDescription: { fontSize: 13, color: COLORS.textMuted, marginTop: 8, lineHeight: 19, fontFamily: fonts.medium, fontWeight: '500' },
  dialogButtonRow: { flexDirection: 'row', justifyContent: 'flex-end', gap: 10, marginTop: 20 },
  dialogButton: { borderWidth: 2, borderColor: COLORS.border, borderRadius: 12, paddingHorizontal: 18, paddingVertical: 11, minWidth: 90, alignItems: 'center', justifyContent: 'center' },
  dialogNeutralButton: { backgroundColor: '#F3F4F6' },
  dialogNeutralText: { color: COLORS.textPrimary, fontFamily: fonts.bold, fontWeight: '700', fontSize: 13 },
  dialogDangerButton: { backgroundColor: '#ED2553' },
  dialogDangerText: { color: '#FFFFFF', fontFamily: fonts.bold, fontWeight: '700', fontSize: 13 },
});

export default AdminReportListScreen;
