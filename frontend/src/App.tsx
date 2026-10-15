import {
  BrowserRouter,
  Navigate,
  Route,
  Routes,
} from 'react-router-dom'
import { AuthProvider } from './auth/AuthContext'
import { ProtectedRoute } from './auth/ProtectedRoute'
import AdminLayout from './layouts/AdminLayout'
import AppLayout from './layouts/AppLayout'
import AdminAuditPage from './pages/AdminAuditPage'
import AdminGroupsPage from './pages/AdminGroupsPage'
import AdminPermissionsPage from './pages/AdminPermissionsPage'
import AdminUsersPage from './pages/AdminUsersPage'
import FilesPage from './pages/FilesPage'
import LoginPage from './pages/LoginPage'
import TrashPage from './pages/TrashPage'

export default function App() {
  return (
    <BrowserRouter>
      <AuthProvider>
        <Routes>
          <Route path="/login" element={<LoginPage />} />

          <Route
            element={
              <ProtectedRoute>
                <AppLayout />
              </ProtectedRoute>
            }
          >
            <Route index element={<FilesPage />} />
            <Route path="/trash" element={<TrashPage />} />

            <Route path="/admin" element={<AdminLayout />}>
              <Route
                index
                element={<Navigate to="users" replace />}
              />
              <Route path="users" element={<AdminUsersPage />} />
              <Route path="groups" element={<AdminGroupsPage />} />
              <Route
                path="permissions"
                element={<AdminPermissionsPage />}
              />
              <Route path="audit" element={<AdminAuditPage />} />
            </Route>
          </Route>

          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </AuthProvider>
    </BrowserRouter>
  )
}