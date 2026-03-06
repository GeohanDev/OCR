import { useEffect, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useAuth } from '../contexts/AuthContext';
import { apiClient } from '../api/client';
import { FileText, Loader2 } from 'lucide-react';

export default function LoginPage() {
  const { isAuthenticated, isLoading, login } = useAuth();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const [demoLoading, setDemoLoading] = useState(false);
  const [demoError, setDemoError] = useState('');

  const authError = searchParams.get('error');
  const authErrorMessages: Record<string, string> = {
    auth_failed: 'Sign-in failed. Please try again.',
    state_mismatch: 'Sign-in failed: response did not match the original request. Please try again.',
    token_exchange_failed: 'Could not exchange the authorisation code — check that Acumatica is configured correctly.',
    empty_token_response: 'Acumatica returned an empty token. Check your OAuth client configuration.',
    no_jwt_in_token_response: 'Acumatica did not return a JWT. Ensure the Connected Application has the openid scope enabled.',
    session_expired: 'Your Acumatica session has expired. Please sign in again.',
  };

  useEffect(() => {
    if (!isLoading && isAuthenticated) {
      navigate('/dashboard', { replace: true });
    }
  }, [isAuthenticated, isLoading, navigate]);

  const handleDemoLogin = async () => {
    setDemoLoading(true);
    setDemoError('');
    try {
      const res = await apiClient.post('/auth/demo-login');
      await login(res.data.accessToken);
      navigate('/dashboard', { replace: true });
    } catch {
      setDemoError('Demo sign-in failed. Please check that the API is running.');
    } finally {
      setDemoLoading(false);
    }
  };

  const handleAcumaticaLogin = () => {
    const clientId = import.meta.env.VITE_ACUMATICA_CLIENT_ID ?? '';
    const redirectUri = `${window.location.origin}/auth/callback`;
    const state = crypto.randomUUID();
    sessionStorage.setItem('oauth_state', state);
    const url =
      `${import.meta.env.VITE_ACUMATICA_URL}/identity/connect/authorize` +
      `?client_id=${encodeURIComponent(clientId)}` +
      `&response_type=code` +
      `&scope=${encodeURIComponent('api openid profile email')}` +
      `&redirect_uri=${encodeURIComponent(redirectUri)}` +
      `&state=${encodeURIComponent(state)}` +
      `&prompt=login`;
    window.location.href = url;
  };

  if (isLoading) {
    return <div className="flex items-center justify-center min-h-screen text-gray-500">Loading...</div>;
  }

  return (
    <div className="min-h-screen bg-gray-50 flex items-center justify-center p-4">
      <div className="card p-8 w-full max-w-sm text-center space-y-6">
        {/* Header */}
        <div className="flex flex-col items-center gap-3">
          <div className="p-3 bg-blue-600 rounded-xl">
            <FileText className="h-8 w-8 text-white" />
          </div>
          <h1 className="text-2xl font-bold text-gray-900">OCR ERP System</h1>
          <p className="text-sm text-gray-500">Sign in to continue</p>
        </div>

        {/* OAuth error from callback */}
        {authError && (
          <div className="text-sm text-red-600 bg-red-50 border border-red-200 rounded-lg p-3 text-left">
            {authErrorMessages[authError] ?? `Sign-in error: ${authError}`}
          </div>
        )}

        {/* Demo sign-in error */}
        {demoError && (
          <div className="text-sm text-red-600 bg-red-50 border border-red-200 rounded-lg p-3 text-left">
            {demoError}
          </div>
        )}

        {/* Primary: demo login (always works in this deployment) */}
        <button
          onClick={handleDemoLogin}
          disabled={demoLoading}
          className="btn-primary w-full py-3 text-base flex items-center justify-center gap-2"
        >
          {demoLoading && <Loader2 className="h-4 w-4 animate-spin" />}
          Sign In
        </button>

        {/* Divider */}
        <div className="flex items-center gap-3">
          <div className="flex-1 h-px bg-gray-200" />
          <span className="text-xs text-gray-400">or</span>
          <div className="flex-1 h-px bg-gray-200" />
        </div>

        {/* Secondary: Acumatica OAuth (for live deployments) */}
        <button
          onClick={handleAcumaticaLogin}
          className="btn-secondary w-full py-2.5 text-sm"
        >
          Sign in with Acumatica
        </button>

        <p className="text-xs text-gray-400">
          Secure OAuth 2.0 authentication via Acumatica ERP
        </p>
      </div>
    </div>
  );
}
