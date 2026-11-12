import { useCallback } from 'react'
import { useNavigate } from 'react-router-dom'
import { useAuth } from './AuthContext'

/**
 * For System Administrators: open one investment group exactly as its Investment Group Administrator sees it
 * (the yellow bar leads back to system administration). This is the only way into restaurant-level screens.
 */
export function useViewAs() {
  const { selectGroup } = useAuth()
  const navigate = useNavigate()
  return useCallback(
    (groupId: number, page = '/enter') => {
      selectGroup(groupId)
      navigate(page)
    },
    [selectGroup, navigate],
  )
}
