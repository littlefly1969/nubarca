import { BrowserRouter, Navigate, Route, Routes } from 'react-router';
import { I18nProvider } from './i18n';
import { ThemeProvider } from './theme';
import { AuthProvider } from './auth/AuthProvider';
import { ProtectedRoute } from './auth/ProtectedRoute';
import { Layout } from './components/Layout';
import { AccountPage } from './pages/AccountPage';
import { AdminImportPage } from './pages/AdminImportPage';
import { AdminJobsPage } from './pages/AdminJobsPage';
import { AdminStatsPage } from './pages/AdminStatsPage';
import { AdminUsersPage } from './pages/AdminUsersPage';
import { AlbumDetailPage } from './pages/AlbumDetailPage';
import { AlbumsPage } from './pages/AlbumsPage';
import { CloudFunctionsPage } from './pages/CloudFunctionsPage';
import { MediaLibraryPage } from './pages/MediaLibraryPage';
import { LegacyMediaRedirect } from './media/workspace/LegacyMediaRedirect';
import { SimilarPhotosExplorerPage } from './pages/SimilarPhotosExplorerPage';
import { HomePage } from './pages/HomePage';
import { LoginPage } from './pages/LoginPage';
import { PeoplePage } from './pages/PeoplePage';
import { PersonDetailPage } from './pages/PersonDetailPage';
import { PlatesPage } from './pages/PlatesPage';
import { AestheticsLabPage } from './pages/AestheticsLabPage';
import { LAB_DEFAULT_ROUTE, LaboratoryPage } from './pages/LaboratoryPage';
import { PrivateVaultPage } from './pages/PrivateVaultPage';
import { SharedAlbumDetailPage } from './pages/SharedAlbumDetailPage';
import { SharedAlbumsPage } from './pages/SharedAlbumsPage';
import { SharesPage } from './pages/SharesPage';
import { LegacyCloudToolRedirect } from './cloud/LegacyCloudToolRedirect';
import { TrashPage } from './pages/TrashPage';
import { TvPage } from './pages/TvPage';
import { TvPairApprovalPage } from './pages/TvPairApprovalPage';
import { PartyPage } from './pages/PartyPage';
import { PartyUploadPage } from './pages/PartyUploadPage';
import { PartyUploadsPage } from './pages/PartyUploadsPage';
import { BeautyLabUploadPage } from './pages/BeautyLabUploadPage';

export function App() {
  return (
    <BrowserRouter>
      {/* The theme is already painted by the bootstrap in index.html; this
          provider takes ownership of the same value without changing it. */}
      <ThemeProvider>
      <I18nProvider>
      <AuthProvider>
        <Routes>
          <Route path="/login" element={<LoginPage />} />
          <Route path="/tv" element={<TvPage />} />
          {/* PUBLIC, unauthenticated party album landing (QR target). */}
          <Route path="/party/:token" element={<PartyPage />} />
          {/* PUBLIC, unauthenticated party UPLOAD landing (separate upload QR). */}
          <Route path="/party/:token/upload" element={<PartyUploadPage />} />
          {/* PUBLIC, unauthenticated TV Beauty Lab mobile UPLOAD landing (QR target). */}
          <Route path="/beauty-lab-upload/:token" element={<BeautyLabUploadPage />} />
          <Route
            path="/tv/pair"
            element={
              <ProtectedRoute>
                <TvPairApprovalPage />
              </ProtectedRoute>
            }
          />
          <Route
            element={
              <ProtectedRoute>
                <Layout />
              </ProtectedRoute>
            }
          >
            <Route path="/" element={<HomePage />} />
            <Route path="/albums" element={<AlbumsPage />} />
            <Route path="/albums/:albumId" element={<AlbumDetailPage />} />
            <Route path="/albums/:albumId/party-uploads" element={<PartyUploadsPage />} />
            {/* SHARE-ALBUM-01: albums OTHER people own and shared with this
                user. Kept apart from /albums so "whose album is this" is never
                ambiguous, and behind the same ProtectedRoute — a live share is
                an authenticated surface, not a public link. */}
            <Route path="/shared-albums" element={<SharedAlbumsPage />} />
            <Route path="/shared-albums/:albumId" element={<SharedAlbumDetailPage />} />
            {/* Slice 5: the unified media workspace. `scope` comes from the
                route; a distinct key per scope remounts the page (clean reset). */}
            <Route path="/media" element={<MediaLibraryPage key="active" scope="active" />} />
            <Route path="/media/excluded" element={<MediaLibraryPage key="excluded" scope="excluded" />} />
            {/* Legacy routes now redirect into the unified workspace, preserving
                compatible query params. */}
            <Route path="/gallery" element={<LegacyMediaRedirect kind="image" scope="active" />} />
            <Route path="/gallery/excluded" element={<LegacyMediaRedirect kind="image" scope="excluded" />} />
            <Route path="/videos" element={<LegacyMediaRedirect kind="video" scope="active" />} />
            <Route path="/videos/excluded" element={<LegacyMediaRedirect kind="video" scope="excluded" />} />
            {/* Dedicated owner-private Similar Photos Explorer for one image. */}
            <Route
              path="/gallery/files/:fileId/similar"
              element={<SimilarPhotosExplorerPage />}
            />
            {/* People v0: owner-private face grouping. */}
            <Route path="/people" element={<PeoplePage />} />
            <Route path="/people/:personId" element={<PersonDetailPage />} />
            {/* Laboratory (Laboratorio): ONE primary destination whose sections
                are routes, so a tab survives refresh, is linkable and moves
                Back/Forward. Plates and Aesthetics keep their own APIs,
                storage and data models — only the shell is shared. */}
            <Route path="/lab" element={<LaboratoryPage />}>
              <Route index element={<Navigate to={LAB_DEFAULT_ROUTE} replace />} />
              <Route path="plates" element={<PlatesPage />} />
              <Route path="aesthetics" element={<AestheticsLabPage />} />
            </Route>
            {/* Plates was a top-level destination before the Laboratory existed;
                the old deep link keeps working. */}
            <Route path="/plates" element={<Navigate to="/lab/plates" replace />} />
            <Route path="/shares" element={<SharesPage />} />
            {/* Upload and TV Devices are Cloud Functions tools now. Their old
                standalone routes stay valid and redirect to the canonical tool
                URL, so existing bookmarks keep working. */}
            <Route path="/tv-devices" element={<LegacyCloudToolRedirect tool="tv-devices" />} />
            <Route path="/upload" element={<LegacyCloudToolRedirect tool="upload" />} />
            <Route path="/cloud-functions" element={<CloudFunctionsPage />} />
            <Route path="/private" element={<PrivateVaultPage />} />
            <Route path="/trash" element={<TrashPage />} />
            {/* Self-service account settings (own password change). */}
            <Route path="/account" element={<AccountPage />} />
            {/* /admin lives behind the same ProtectedRoute. The page itself
                handles 403 from the backend for non-admin users. */}
            <Route path="/admin" element={<AdminStatsPage />} />
            {/* Slice 81: admin-only server-side import. The page + backend
                both gate non-admins (403). */}
            <Route path="/admin/import" element={<AdminImportPage />} />
            {/* Slice 90: admin-only background-jobs dashboard. */}
            <Route path="/admin/jobs" element={<AdminJobsPage />} />
            {/* Admin user management: list/create/reset password/grant admin/
                enable-disable. The page + backend both gate non-admins (403). */}
            <Route path="/admin/users" element={<AdminUsersPage />} />
          </Route>
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </AuthProvider>
      </I18nProvider>
      </ThemeProvider>
    </BrowserRouter>
  );
}
