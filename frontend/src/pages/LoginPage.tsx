import { useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import { useAuth } from '../contexts/AuthContext';
import { FileText } from 'lucide-react';

export default function LoginPage() {
  const { isAuthenticated, isLoading } = useAuth();
  const navigate = useNavigate();

  useEffect(() => {
    if (!isLoading && isAuthenticated) {
      navigate('/dashboard', { replace: true });
    }
  }, [isAuthenticated, isLoading, navigate]);

  const handleLogin = () => {
    // Redirect to Acumatica OAuth2 authorization endpoint
    const params = new URLSearchParams({
      response_type: 'code',
      client_id: import.meta.env.VITE_ACUMATICA_CLIENT_ID ?? '',
      redirect_uri: `${window.location.origin}/auth/callback`,
      scope: 'openid profile email',
    });
    window.location.href = `${import.meta.env.VITE_ACUMATICA_URL}/identity/connect/authorize?${params}`;
  };

  if (isLoading) {
    return <div className="flex items-center justify-center min-h-screen text-gray-500">Loading...</div>;
  }

  return (
    <div className="min-h-screen bg-gray-50 flex items-center justify-center p-4">
      <div className="card p-8 w-full max-w-sm text-center space-y-6">
        <div className="flex flex-col items-center gap-3">
          <div className="p-3 bg-blue-600 rounded-xl">
            <FileText className="h-8 w-8 text-white" />
          </div>
          <h1 className="text-2xl font-bold text-gray-900">OCR ERP System</h1>
          <p className="text-sm text-gray-500">Sign in with your Acumatica account to continue</p>
        </div>
        <button onClick={handleLogin} className="btn-primary w-full py-3 text-base">
          Sign in with Acumatica
        </button>
        <p className="text-xs text-gray-400">
          Secure OAuth 2.0 authentication via Acumatica ERP
        </p>
      </div>
    </div>
  );
}
