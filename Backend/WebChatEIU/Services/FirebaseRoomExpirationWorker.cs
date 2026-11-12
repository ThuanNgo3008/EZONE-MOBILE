using Google.Cloud.Firestore;

namespace WebChatEIU.Services
{
    public sealed class FirebaseRoomExpirationWorker : BackgroundService
    {
        private readonly FirestoreDb _firestoreDb;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<FirebaseRoomExpirationWorker> _logger;

        public FirebaseRoomExpirationWorker(
            FirestoreDb firestoreDb,
            IServiceScopeFactory scopeFactory,
            ILogger<FirebaseRoomExpirationWorker> logger)
        {
            _firestoreDb = firestoreDb;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(
            CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var now = Timestamp.FromDateTime(DateTime.UtcNow);

                    await ProcessExpiredReservationsAsync(now, stoppingToken);

                    await ProcessStaleWaitingAsync(stoppingToken);

                    await ProcessPendingCleanupAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to process expired chat rooms.");
                }

                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(10),
                        stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }


        private async Task ProcessPendingCleanupAsync(
            CancellationToken cancellationToken)
        {
            var snapshot = await _firestoreDb
                .Collection("chatRooms")
                .WhereEqualTo("Status", "Closed")
                .WhereEqualTo("CleanupPending", true)
                .Limit(100)
                .GetSnapshotAsync(cancellationToken);

            using var scope = _scopeFactory.CreateScope();

            var cleanupService = scope.ServiceProvider
                .GetRequiredService<FirebaseTemporaryMessageCleanupService>();

            foreach (var document in snapshot.Documents)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    int deletedCount = await cleanupService
                        .DeleteTemporaryMessagesAsync(document.Id);

                    await document.Reference.UpdateAsync(
                        new Dictionary<string, object>
                        {
                            ["CleanupPending"] = false,
                            ["UpdatedAt"] = Timestamp.FromDateTime(
                                DateTime.UtcNow)
                        });

                    _logger.LogInformation(
                        "Cleaned up room {RoomId}: {Count} messages.",
                        document.Id,
                        deletedCount);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to clean up room {RoomId}.",
                        document.Id);
                }
            }
        }


        private async Task ProcessStaleWaitingAsync(
            CancellationToken cancellationToken)
        {
            var cutoff = Timestamp.FromDateTime(
                DateTime.UtcNow.AddSeconds(-60));

            var snapshot = await _firestoreDb
                .Collection("randomMatchQueue")
                .WhereEqualTo("Status", "Waiting")
                .WhereLessThanOrEqualTo("LastSeenAt", cutoff)
                .Limit(100)
                .GetSnapshotAsync(cancellationToken);

            using var scope = _scopeFactory.CreateScope();

            var queueService = scope.ServiceProvider
                .GetRequiredService<FirebaseRandomMatchQueueService>();

            foreach (var document in snapshot.Documents)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    bool removed = await queueService
                        .TryRemoveStaleWaitingAsync(document.Id);

                    if (removed)
                    {
                        _logger.LogInformation(
                            "Removed inactive user {UserId} from Random Match Queue.",
                            document.Id);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to remove inactive user {UserId}.",
                        document.Id);
                }
            }
        }



        private async Task ProcessExpiredReservationsAsync(
            Timestamp now,
            CancellationToken cancellationToken)
        {
            var snapshot = await _firestoreDb
                .Collection("matchReservations")
                .WhereEqualTo("Status", "PendingReady")
                .WhereLessThanOrEqualTo("ReadyExpiresAt", now)
                .Limit(100)
                .GetSnapshotAsync(cancellationToken);

            using var scope = _scopeFactory.CreateScope();

            var reservationService = scope.ServiceProvider
                .GetRequiredService<FirebaseMatchReservationService>();

            foreach (var document in snapshot.Documents)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    bool expired = await reservationService
                        .TryExpireReservationAsync(document.Id);

                    if (expired)
                    {
                        _logger.LogInformation(
                            "Expired match reservation {ReservationId}.",
                            document.Id);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to expire match reservation {ReservationId}.",
                        document.Id);
                }
            }
        }

    }

}
