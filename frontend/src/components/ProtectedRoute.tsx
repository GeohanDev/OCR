import { Navigate, Outlet, useLocation } from 'react-router-dom';
import { useAuth } from '../contexts/AuthContext';

interface ProtectedRouteProps {
  requireAdmin?: boolean;
  requireManager?: boolean;
}

export default function ProtectedRoute({ requireAdmin, requireManager }: ProtectedRouteProps) {
  const { isAuthenticated, isLoading, isAdmin, isManagerOrAbove } = useAuth();
  const location = useLocation();

  if (isLoading) {
    return <div className="flex items-center justify-center min-h-screen text-gray-500">Loading...</div>;
  }

  if (!isAuthenticated) {
    return <Navigate to="/login" state={{ from: location }} replace />;
  }

  if (requireAdmin && !isAdmin) {
    return <Navigate to="/dashboard" replace />;
  }

  if (requireManager && !isManagerOrAbove) {
    return <Navigate to="/dashboard" replace />;
  }

  return <Outlet />;
}
