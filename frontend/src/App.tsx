import { BrowserRouter, Navigate, Route, Routes } from 'react-router';
import { I18nProvider } from './i18n';
import { ThemeProvider } from './theme';
import { AuthProvider } from './auth/AuthProvider';
import { PermissionRoute } from './auth/PermissionRoute';
import { ProtectedRoute } from './auth/ProtectedRoute';
import { PERMISSIONS } from '@nubarca/api-client';
import { Layout } from './components/Layout';
import { AccountPage } from './pages/AccountPage';
import { AdminImportPage } from './pages/AdminImportPage';
import { AdminJobsPage } from './pages/AdminJobsPage';
import { AdminRolesPage } from './pages/AdminRolesPage';
import { AdminStatsPage } from './pages/AdminStatsPage';
import { AdminUsersPage } from './pages/AdminUsersPage';
import { AlbumDetailPage } from './pages/AlbumDetailPage';
import { HelpPage } from './pages/HelpPage';
import { AlbumsPage } from './pages/AlbumsPage';
import { CloudFunctionsPage } from './pages/CloudFunctionsPage';
import { MediaLibraryPage } from './pages/MediaLibraryPage';
import { LegacyMediaRedirect } from './media/workspace/LegacyMediaRedirect';
import { SimilarPhotosExplorerPage } from './pages/SimilarPhotosExplorerPage';
import { HomePage } from './pages/HomePage';
import { LoginPage } from './pages/LoginPage';
import { ForgotPasswordPage } from './pages/ForgotPasswordPage';
import { ResetPasswordPage } from './pages/ResetPasswordPage';
import { PeoplePage } from './pages/PeoplePage';
import { PersonDetailPage } from './pages/PersonDetailPage';
import { PlatesPage } from './pages/PlatesPage';
import { AestheticsLabPage } from './pages/AestheticsLabPage';
import { LaboratoryIndexRedirect, LaboratoryPage } from './pages/LaboratoryPage';
import { PrivateVaultPage } from './pages/PrivateVaultPage';
import { SharedAlbumDetailPage } from './pages/SharedAlbumDetailPage';
import { LegacySharedAlbumsRedirect } from './albums/LegacySharedAlbumsRedirect';
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
          {/* PUBLIC password recovery. Unauthenticated by necessity — the
              person asking cannot sign in. */}
          <Route path="/forgot-password" element={<ForgotPasswordPage />} />
          <Route path="/reset-password" element={<ResetPasswordPage />} />
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
            <Route path="/help" element={<HelpPage />} />
            <Route path="/albums" element={<AlbumsPage />} />
            <Route path="/albums/:albumId" element={<AlbumDetailPage />} />
            <Route path="/albums/:albumId/party-uploads" element={<PartyUploadsPage />} />
            {/* "Shared with me" is no longer a destination: /albums holds both
                collections. The old list route keeps working as a redirect, and
                the per-album route stays exactly where it is — it is the
                RECIPIENT's album, backed by the recipient's authority, and
                owner and recipient must never resolve to one route. */}
            <Route path="/shared-albums" element={<LegacySharedAlbumsRedirect />} />
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
            {/* People v0: owner-private face grouping. The guard produces a
                clean forbidden state for a direct navigation; the backend
                refuses every /api/people call independently. */}
            <Route
              path="/people"
              element={
                <PermissionRoute permissions={[PERMISSIONS.peopleAccess]}>
                  <PeoplePage />
                </PermissionRoute>
              }
            />
            <Route
              path="/people/:personId"
              element={
                <PermissionRoute permissions={[PERMISSIONS.peopleAccess]}>
                  <PersonDetailPage />
                </PermissionRoute>
              }
            />
            {/* Laboratory (Laboratorio): ONE primary destination whose sections
                are routes, so a tab survives refresh, is linkable and moves
                Back/Forward. Plates and Aesthetics keep their own APIs,
                storage and data models — only the shell is shared. */}
            <Route
              path="/lab"
              element={
                <PermissionRoute permissions={[PERMISSIONS.laboratoryAccess]}>
                  <LaboratoryPage />
                </PermissionRoute>
              }
            >
              <Route index element={<LaboratoryIndexRedirect />} />
              {/* A section needs the shell permission AND its own — the same
                  composite the server's Laboratory policies require. */}
              <Route
                path="plates"
                element={
                  <PermissionRoute
                    permissions={[PERMISSIONS.laboratoryAccess, PERMISSIONS.laboratoryPlates]}
                  >
                    <PlatesPage />
                  </PermissionRoute>
                }
              />
              <Route
                path="aesthetics"
                element={
                  <PermissionRoute
                    permissions={[PERMISSIONS.laboratoryAccess, PERMISSIONS.laboratoryAesthetics]}
                  >
                    <AestheticsLabPage />
                  </PermissionRoute>
                }
              />
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
            <Route
              path="/cloud-functions"
              element={
                <PermissionRoute permissions={[PERMISSIONS.cloudFunctionsAccess]}>
                  <CloudFunctionsPage />
                </PermissionRoute>
              }
            />
            <Route
              path="/private"
              element={
                <PermissionRoute permissions={[PERMISSIONS.privateVaultAccess]}>
                  <PrivateVaultPage />
                </PermissionRoute>
              }
            />
            <Route path="/trash" element={<TrashPage />} />
            {/* Self-service account settings (own password change). */}
            <Route path="/account" element={<AccountPage />} />
            {/* Each administration destination carries its OWN permission, so
                holding one never opens another — the same separation the four
                admin APIs enforce server-side. */}
            <Route
              path="/admin"
              element={
                <PermissionRoute permissions={[PERMISSIONS.adminDashboard]}>
                  <AdminStatsPage />
                </PermissionRoute>
              }
            />
            <Route
              path="/admin/import"
              element={
                <PermissionRoute permissions={[PERMISSIONS.adminImport]}>
                  <AdminImportPage />
                </PermissionRoute>
              }
            />
            <Route
              path="/admin/jobs"
              element={
                <PermissionRoute permissions={[PERMISSIONS.adminJobsManage]}>
                  <AdminJobsPage />
                </PermissionRoute>
              }
            />
            <Route
              path="/admin/users"
              element={
                <PermissionRoute permissions={[PERMISSIONS.adminUsersManage]}>
                  <AdminUsersPage />
                </PermissionRoute>
              }
            />
            {/* Editing roles is its own authority: a user manager may assign a
                role and can never change what one contains. */}
            <Route
              path="/admin/roles"
              element={
                <PermissionRoute permissions={[PERMISSIONS.adminRolesManage]}>
                  <AdminRolesPage />
                </PermissionRoute>
              }
            />
          </Route>
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </AuthProvider>
      </I18nProvider>
      </ThemeProvider>
    </BrowserRouter>
  );
}
