import { useEffect, useRef } from 'react';
import { useNavigate } from 'react-router-dom';
import { useAuth } from '../contexts/AuthContext';
import { apiClient } from '../api/client';

export default function AuthCallbackPage() {
  const { login } = useAuth();
  const navigate = useNavigate();
  const handled = useRef(false);

  useEffect(() => {
    if (handled.current) return;
    handled.current = true;

    const params = new URLSearchParams(window.location.search);
    const code = params.get('code');
    const returnedState = params.get('state');
    const expectedState = sessionStorage.getItem('oauth_state');
    sessionStorage.removeItem('oauth_state');

    if (!code) {
      navigate('/login', { replace: true });
      return;
    }

    // Only reject if state was returned but doesn't match (CSRF).
    // If Acumatica omits state (e.g. first-time consent flow), allow the request through.
    if (returnedState && returnedState !== expectedState) {
      navigate('/login?error=state_mismatch', { replace: true });
      return;
    }

    apiClient
      .post('/auth/callback', { code, redirectUri: `${window.location.origin}/auth/callback` })
      .then(async (res) => {
        await login(res.data.accessToken, res.data.acumaticaToken);
        navigate('/dashboard', { replace: true });
      })
      .catch((err) => {
        const errorCode: string = err.response?.data?.error ?? 'auth_failed';
        navigate(`/login?error=${encodeURIComponent(errorCode)}`, { replace: true });
      });
  }, []);

  return (
    <div className="flex items-center justify-center min-h-screen text-gray-500">
      Completing sign in...
    </div>
  );
}
